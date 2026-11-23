using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyLive.Core.Audio;

/// <summary>
/// Wraps a NAudio capture device and converts whatever the device mix format is to 48 kHz stereo float32.
/// Chunk timestamps form a contiguous timeline (re-synced to arrival time only on drift beyond the tolerance).
/// </summary>
public abstract class CaptureAudioInput : IAudioInput, IDisposable
{
    private const int OutRate = AudioTiming.SampleRate;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _stereo48k;
    private readonly float[] _readBuf = new float[OutRate / 10 * 2];     // up to 100 ms per read
    private long _nextTs;
    private bool _hasTs;
    private int _stopped;

    public event Action<AudioChunk>? Data;
    public event Action? Lost;

    protected CaptureAudioInput(IWaveIn capture)
    {
        _capture = capture;
        _buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            ReadFully = false,
        };
        ISampleProvider p = _buffer.ToSampleProvider();
        if (p.WaveFormat.SampleRate != OutRate) p = new WdlResamplingSampleProvider(p, OutRate);
        if (p.WaveFormat.Channels != 2) p = new DownmixProvider(p);
        _stereo48k = p;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
    }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            long arrival = Stopwatch.GetTimestamp();
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int n;
            while ((n = _stereo48k.Read(_readBuf, 0, _readBuf.Length)) > 0)
            {
                long dur = AudioTiming.Duration(n);
                // WASAPI delivers packets in bursts, so arrival time is only a coarse estimate of the first
                // sample's time: keep a running contiguous timeline and re-sync only on real drift.
                long estimate = arrival - dur;
                if (!_hasTs || Math.Abs(estimate - _nextTs) > AudioTiming.ResyncTolerance)
                {
                    _nextTs = estimate;
                    _hasTs = true;
                }
                long ts = _nextTs;
                _nextTs += dur;
                Data?.Invoke(new AudioChunk(_readBuf.AsSpan(0, n).ToArray(), ts));
            }
        }
        catch { /* never let an exception escape a NAudio callback thread */ }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || Interlocked.Exchange(ref _stopped, 1) == 1) return;
        try { Lost?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { }
        _capture.RecordingStopped -= OnStopped;
        _capture.Dispose();
    }

    /// <summary>Adapts any non-stereo channel count to stereo via <see cref="ChannelDownmix"/>.</summary>
    private sealed class DownmixProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private float[] _tmp = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }
        public DownmixProvider(ISampleProvider src)
        {
            _src = src;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _src.WaveFormat.Channels, frames = count / 2;
            if (_tmp.Length < frames * ch) _tmp = new float[frames * ch];
            int got = _src.Read(_tmp, 0, frames * ch) / ch * ch;
            if (got == 0) return 0;
            var stereo = ChannelDownmix.ToStereo(_tmp.AsSpan(0, got).ToArray(), ch);
            Array.Copy(stereo, 0, buffer, offset, stereo.Length);
            return stereo.Length;
        }
    }
}
