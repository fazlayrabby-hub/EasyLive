using System.Diagnostics;
using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

public sealed partial class FfmpegStreamSession
{
    private static readonly long Freq = Stopwatch.Frequency;
    private Action<nint>? _onAudioPacket;
    private bool _attachExtradata;           // next video packet carries the new SPS/PPS (after a re-open)
    private bool _liveChangeUnverified;      // a live bitrate change was applied; the next send proves it
    private bool _liveBitrateRejected;       // this encoder refused a live change: re-open from now on

    private unsafe void EncodeMain()
    {
        try { EncodeLoop(); }
        catch (Exception ex)
        {
            FfmpegRuntime.Log("encode", $"crashed: {ex}");
            RaiseFailed(ErrorTexts.Unexpected);
        }
        finally { _queue.Complete(); }
    }

    private unsafe void EncodeLoop()
    {
        AVPacket* scratch = ffmpeg.av_packet_alloc();
        try
        {
            using var waiter = new PreciseWaiter(_stopEvent);
            _onAudioPacket = p => EnqueueOwned((AVPacket*)p, isVideo: false);

            // Never encode an invented picture: wait for the first real frame.
            long giveUp = Stopwatch.GetTimestamp() + FirstFrameTimeoutMs * Freq / 1000;
            while (!_converter.HasFrame)
            {
                if (_stopping) return;
                if (Stopwatch.GetTimestamp() > giveUp) { RaiseFailed(ErrorTexts.NoPicture); return; }
                WaitHandle.WaitAny(new WaitHandle[] { _stopEvent, _frameEvent }, 50);
            }

            long start = Stopwatch.GetTimestamp();   // shared A/V zero: video tick n and audio sample s are both relative to it
            var clock = new VideoClock(start, Freq, _video!.Fps);
            var audioLine = new AudioTimeline(start, Freq);
            long n = 0;
            FfmpegRuntime.Log("encode", "first frame; streaming");

            while (!_stopping)
            {
                if (!waiter.WaitUntil(clock.TickQpc(n)) || _stopping) break;
                long now = Stopwatch.GetTimestamp();
                long behind = clock.TickIndexAt(now) - n;
                if (behind >= 2)   // fell behind real time: skip ticks instead of building up latency
                {
                    Interlocked.Add(ref _skippedTicks, (int)behind);
                    n += behind;
                }
                Volatile.Write(ref _encodeLagMs, (int)Math.Max(0, (now - clock.TickQpc(n)) * 1000 / Freq));

                if (!ApplyPending(ref clock, ref n, now, scratch)) return;
                if (!EncodeAudio(audioLine, start, now, scratch)) return;
                if (!EncodeVideoTick(n, scratch, out bool liveChangeRejected)) return;
                if (liveChangeRejected)
                {
                    // The encoder refused the live bitrate change: use the re-open path from now on (same connection).
                    _liveBitrateRejected = true;
                    var v = _video!;
                    if (!ReopenVideo(v.Width, v.Height, v.Fps, v.Kbps, ref clock, ref n, now, scratch, drainOld: false)) return;
                    FfmpegRuntime.Log("encode", $"live bitrate change rejected by {v.CodecName}; re-opened at {v.Kbps} kbps");
                    continue;   // ReopenVideo moved n to the next tick
                }
                n++;
            }
            if (Volatile.Read(ref _failed) == 0) FlushEncoders(scratch);
        }
        finally { ffmpeg.av_packet_free(&scratch); }
    }

    private unsafe bool EncodeVideoTick(long n, AVPacket* scratch, out bool liveChangeRejected)
    {
        liveChangeRejected = false;
        _converter.TryConvertLatest(_videoFrame);   // false = nothing new: repeat the last picture
        _videoFrame->pts = n;
        _videoFrame->pict_type = _queue.TakeKeyframeRequest() ? AVPictureType.AV_PICTURE_TYPE_I : AVPictureType.AV_PICTURE_TYPE_NONE;
        int r = _video!.Send(_videoFrame);
        bool unverified = _liveChangeUnverified;
        _liveChangeUnverified = false;
        if (r < 0 && r != FfErrors.EAgain)
        {
            FfmpegRuntime.Log("encode", $"video send: {FfmpegRuntime.Describe(r)}");
            if (unverified)
            {
                liveChangeRejected = true;
                return true;
            }
            RaiseFailed(ErrorTexts.EncoderFailed);
            return false;
        }
        return DrainVideo(scratch);
    }

    private unsafe bool DrainVideo(AVPacket* scratch)
    {
        while (true)
        {
            int r = _video!.Receive(scratch);
            if (r == FfErrors.EAgain || r == ffmpeg.AVERROR_EOF) return true;
            if (r < 0)
            {
                FfmpegRuntime.Log("encode", $"video receive: {FfmpegRuntime.Describe(r)}");
                RaiseFailed(ErrorTexts.EncoderFailed);
                return false;
            }
            if (_attachExtradata)
            {
                var extra = _video.Extradata;
                if (extra.Length > 0)
                {
                    byte* side = ffmpeg.av_packet_new_side_data(scratch, AVPacketSideDataType.AV_PKT_DATA_NEW_EXTRADATA, (ulong)extra.Length);
                    if (side != null) extra.CopyTo(new Span<byte>(side, extra.Length));
                }
                _attachExtradata = false;
            }
            AVPacket* p = ffmpeg.av_packet_alloc();
            ffmpeg.av_packet_move_ref(p, scratch);
            EnqueueOwned(p, isVideo: true);
        }
    }

    /// <summary>Rescales an encoder packet to the muxer stream and queues it (the queue owns it from here).</summary>
    private unsafe void EnqueueOwned(AVPacket* p, bool isVideo)
    {
        var encTb = isVideo ? _video!.TimeBase : _audio!.TimeBase;
        var stTb = isVideo ? _muxer!.VideoTimeBase : _muxer!.AudioTimeBase;
        p->stream_index = isVideo ? _muxer.VideoIndex : _muxer.AudioIndex;
        ffmpeg.av_packet_rescale_ts(p, encTb, stTb);
        long ts = p->dts != ffmpeg.AV_NOPTS_VALUE ? p->dts : p->pts;
        long dtsMs = ffmpeg.av_rescale_q(ts, stTb, new AVRational { num = 1, den = 1000 });
        bool key = !isVideo || (p->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        _queue.Enqueue((nint)p, isVideo, key, dtsMs);
    }

    private unsafe bool EncodeAudio(AudioTimeline line, long start, long now, AVPacket* scratch)
    {
        for (int budget = 100; budget > 0 && _audioIn.TryDequeue(out var c); budget--)
        {
            int frames = c.Samples.Length / 2;
            var (pts, skip, silence) = line.Place(c.QpcTicks, frames);
            if (skip >= frames && silence == 0) continue;
            if (!WriteAudio(c.Samples, pts, Math.Min(skip, frames), silence, scratch)) return false;
        }
        // No audio arriving (no source, device gone): keep the audio track continuous with silence, 250 ms behind
        // real time so slightly late chunks still fit. Platforms (Facebook) drop streams whose audio stops.
        long realSamples = (now - start) * AudioEncoder.SampleRate / Freq;
        if (realSamples - line.NextPts > AudioEncoder.SampleRate / 2)
        {
            long pts = line.NextPts;
            int pad = line.PadTo(realSamples - AudioEncoder.SampleRate / 4);
            if (pad > 0 && !WriteAudio(ReadOnlySpan<float>.Empty, pts, 0, pad, scratch)) return false;
        }
        return true;
    }

    private unsafe bool WriteAudio(ReadOnlySpan<float> samples, long pts, int skip, int silence, AVPacket* scratch)
    {
        int r = _audio!.Write(samples, pts, skip, silence, _onAudioPacket!, scratch);
        if (r >= 0) return true;
        FfmpegRuntime.Log("encode", $"audio: {FfmpegRuntime.Describe(r)}");
        RaiseFailed(ErrorTexts.EncoderFailed);
        return false;
    }

    private unsafe void FlushEncoders(AVPacket* scratch)
    {
        if (_video!.Send(null) >= 0) DrainVideo(scratch);
        int r = _audio!.Flush(_onAudioPacket!, scratch);
        if (r < 0) FfmpegRuntime.Log("encode", $"audio flush: {FfmpegRuntime.Describe(r)}");
    }

    private static unsafe AVFrame* AllocVideoFrame(VideoEncoder v)
    {
        AVFrame* f = ffmpeg.av_frame_alloc();
        if (f == null) return null;
        f->format = (int)v.InputFormat;
        f->width = v.Width;
        f->height = v.Height;
        if (ffmpeg.av_frame_get_buffer(f, 32) < 0) { ffmpeg.av_frame_free(&f); return null; }
        // Black (BT.709 limited range) until the first conversion.
        new Span<byte>(f->data[0], f->linesize[0] * v.Height).Fill(16);
        new Span<byte>(f->data[1], f->linesize[1] * (v.Height / 2)).Fill(128);
        if (v.InputFormat == AVPixelFormat.AV_PIX_FMT_YUV420P)
            new Span<byte>(f->data[2], f->linesize[2] * (v.Height / 2)).Fill(128);
        return f;
    }
}
