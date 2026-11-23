using System.Collections.Concurrent;
using System.Diagnostics;
using EasyLive.Core.Audio;
using EasyLive.Core.Capture;
using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

/// <summary>
/// <see cref="IStreamSession"/> on the FFmpeg libraries. Threads:
/// capture/audio callers only enqueue (<see cref="PushVideo"/> copies the texture on the GPU);
/// the encode thread runs a constant-frame-rate ticker that encodes the latest picture every tick (repeating it on
/// static content) plus the audio, and puts packets in a <see cref="SendQueue{T}"/>;
/// the send thread writes them to the RTMP connection so a slow network never stalls encoding.
/// </summary>
public sealed partial class FfmpegStreamSession : IStreamSession
{
    private const int ConnectTimeoutMs = 10_000;
    private const int WriteTimeoutMs = 10_000;
    private const int MaxQueueMs = 3_000;
    private const int FirstFrameTimeoutMs = 5_000;
    private const int JoinTimeoutMs = 3_000;
    private const int MaxPendingAudioChunks = 500;          // 5 s of 10 ms chunks

    private readonly object _gate = new();
    private readonly ManualResetEvent _stopEvent = new(false);
    private readonly AutoResetEvent _frameEvent = new(false);
    private readonly ConcurrentQueue<AudioChunk> _audioIn = new();
    private readonly FrameConverter _converter = new();
    private readonly SendQueue<nint> _queue;

    private RtmpMuxer? _muxer;
    private VideoEncoder? _video;
    private AudioEncoder? _audio;
    private unsafe AVFrame* _videoFrame;
    private Thread? _encodeThread;
    private Thread? _sendThread;
    private Task? _openTask;
    private Task? _disposeTask;
    private StreamPlan? _plan;

    private int _state;                     // 0 new, 1 starting, 2 running
    private int _failed;
    private volatile bool _stopping;
    private long _publishedAt;              // Stopwatch timestamp of a successful publish
    private int _encodeLagMs;
    private int _skippedTicks;
    private int _currentKbps;
    private int _pendingKbps;
    private (QualityRung Rung, TaskCompletionSource Done)? _pendingReinit;

    public unsafe FfmpegStreamSession()
    {
        _queue = new SendQueue<nint>(MaxQueueMs, p => { var pkt = (AVPacket*)p; ffmpeg.av_packet_free(&pkt); });
    }

    public event Action<string>? Failed;

    public StreamHealth Health =>
        new(_queue.QueueMs, _queue.DroppedTotal + Volatile.Read(ref _skippedTicks), Volatile.Read(ref _encodeLagMs), Volatile.Read(ref _currentKbps));

    // Diagnostics for tests and logs.
    internal int ConnectCount { get; private set; }
    internal int VideoReopenCount { get; private set; }
    internal string? VideoCodecName { get; private set; }

    public async Task StartAsync(string rtmpUrlWithKey, StreamPlan plan, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) throw new InvalidOperationException("StartAsync can only be called once.");
        _plan = plan;
        Volatile.Write(ref _currentKbps, plan.StartKbps);

        if (!FfmpegRuntime.TryInitialize()) throw FailStart(ErrorTexts.ComponentsMissing);
        FfmpegRuntime.AddSecret(UrlMasker.KeyPart(rtmpUrlWithKey));
        FfmpegRuntime.Log("session", $"start {UrlMasker.Mask(rtmpUrlWithKey)} encoder={plan.Encoder} rung={plan.Rung.Name} kbps={plan.StartKbps}");

        var open = Task.Run(() => OpenAll(rtmpUrlWithKey, plan, ct), CancellationToken.None);
        _openTask = open;
        string? failure = await open.ConfigureAwait(false);
        if (ct.IsCancellationRequested) { _stopping = true; throw new OperationCanceledException(ct); }
        if (failure is not null) throw FailStart(failure);
        if (_stopping) throw new ObjectDisposedException(nameof(FfmpegStreamSession));

        _encodeThread = new Thread(EncodeMain) { IsBackground = true, Name = "EasyLive encode", Priority = ThreadPriority.AboveNormal };
        _sendThread = new Thread(SendMain) { IsBackground = true, Name = "EasyLive send" };
        _state = 2;
        _encodeThread.Start();
        _sendThread.Start();
    }

    /// <summary>Opens encoders and the connection (blocking; thread pool). Returns a plain failure text or null.</summary>
    private unsafe string? OpenAll(string url, StreamPlan plan, CancellationToken ct)
    {
        try
        {
            var r = plan.Rung;
            _video = VideoEncoder.Open(plan.Encoder, r.Width, r.Height, r.Fps, plan.StartKbps);
            if (_video is null) return ErrorTexts.EncoderFailed;
            VideoCodecName = _video.CodecName;
            _audio = AudioEncoder.Open();
            if (_audio is null) return ErrorTexts.EncoderFailed;
            _videoFrame = AllocVideoFrame(_video);
            if (_videoFrame == null) return ErrorTexts.EncoderFailed;
            if (ct.IsCancellationRequested || _stopping) return null;

            _muxer = RtmpMuxer.Open(url, _video.Context, _audio.Context, ConnectTimeoutMs, ct, out int err, out var lines);
            if (_muxer is null)
            {
                if (ct.IsCancellationRequested) return null;
                FfmpegRuntime.Log("session", $"connect failed: {FfmpegRuntime.Describe(err)}; {string.Join(" | ", lines)}");
                return ErrorTexts.ForConnectFailure(err, lines);
            }
            ConnectCount++;
            _publishedAt = Stopwatch.GetTimestamp();
            FfmpegRuntime.Log("session", "published");
            return null;
        }
        catch (Exception ex)
        {
            FfmpegRuntime.Log("session", $"open threw {ex}");
            return ErrorTexts.Unexpected;
        }
    }

    private StreamSessionException FailStart(string message)
    {
        RaiseFailed(message);
        return new StreamSessionException(message);
    }

    public void PushVideo(CaptureFrame f)
    {
        if (_stopping || Volatile.Read(ref _state) == 0) return;
        try
        {
            _converter.Ingest(f);
            _frameEvent.Set();
        }
        catch (Exception ex)
        {
            FfmpegRuntime.Log("video", $"ingest failed: {ex.GetType().Name} {ex.Message}");   // never throw into the capture callback
        }
    }

    public void PushAudio(AudioChunk c)
    {
        if (_stopping || Volatile.Read(ref _state) == 0) return;
        _audioIn.Enqueue(c);
        while (_audioIn.Count > MaxPendingAudioChunks && _audioIn.TryDequeue(out _)) { }
    }

    public void SetBitrate(int kbps)
    {
        if (kbps > 0) Volatile.Write(ref _pendingKbps, kbps);
    }

    public Task ReinitAsync(QualityRung lower)
    {
        if (_stopping || Volatile.Read(ref _state) != 2) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingReinit?.Done.TrySetResult();   // superseded
            _pendingReinit = (lower, done);
        }
        return done.Task;
    }

    /// <summary>Raises <see cref="Failed"/> once (on the thread pool so handlers may dispose) and stops the workers.</summary>
    private void RaiseFailed(string message)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        FfmpegRuntime.Log("session", "failed: " + message);
        _stopping = true;
        _stopEvent.Set();
        _muxer?.Abort();
        _queue.Complete();
        lock (_gate) { _pendingReinit?.Done.TrySetResult(); _pendingReinit = null; }
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try { Failed?.Invoke(message); }
            catch (Exception ex) { FfmpegRuntime.Log("session", $"Failed handler threw {ex.GetType().Name}"); }
        }, null);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) _disposeTask ??= Task.Run(DisposeCore);
        return new ValueTask(_disposeTask);
    }

    private unsafe void DisposeCore()
    {
        _stopping = true;
        _stopEvent.Set();
        try { _openTask?.Wait(ConnectTimeoutMs + 2_000); } catch { /* reported via Failed */ }

        bool encodeDone = Join(_encodeThread, JoinTimeoutMs);   // flushes the encoders when stopping normally
        _queue.Complete();
        bool sendDone = Join(_sendThread, JoinTimeoutMs);
        if (!sendDone || !encodeDone)
        {
            _muxer?.Abort();
            sendDone = Join(_sendThread, 1_000);
            encodeDone = Join(_encodeThread, 1_000);
        }
        if (!sendDone || !encodeDone)
        {
            // A worker is stuck inside a native call; freeing what it uses would crash the process. Leak instead.
            FfmpegRuntime.Log("session", "worker threads did not stop in time; native resources left allocated");
            return;
        }

        if (Volatile.Read(ref _failed) == 0 && _muxer is not null && Volatile.Read(ref _state) == 2)
        {
            int r = _muxer.WriteTrailer(1_000);
            if (r < 0) FfmpegRuntime.Log("session", $"trailer: {FfmpegRuntime.Describe(r)}");
        }
        foreach (var p in _queue.DrainAll()) { var pkt = (AVPacket*)p; ffmpeg.av_packet_free(&pkt); }
        _muxer?.Dispose();
        _muxer = null;
        _video?.Dispose();
        _video = null;
        _audio?.Dispose();
        _audio = null;
        var frame = _videoFrame;
        _videoFrame = null;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        _converter.Dispose();
        FfmpegRuntime.Log("session", "stopped");
    }

    private static bool Join(Thread? t, int ms) => t is null || !t.IsAlive || t.Join(ms);
}
