using System.Diagnostics;

namespace EasyLive.Core.Audio;

/// <summary>
/// Mixes game sound (the clock anchor) with an optional microphone. Each system chunk pulls the same
/// number of samples from the mic buffer (silence-padded). Output keeps one running, contiguous QPC timeline:
/// if the wall clock runs more than <see cref="FallbackThreshold"/> ahead of the end of that timeline
/// (system input idle), a timer fills the gap with 10 ms pieces of silence plus mic.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const string NoMicMessage = "Microphone not found — streaming game sound only.";
    public const string MicLostMessage = "Microphone disconnected — streaming game sound only.";
    public const string SystemLostMessage = "Game sound capture stopped.";

    private const int SamplesPer10Ms = 960;                 // 480 frames x 2 channels
    private const int MaxMicBacklog = 19200;                // 200 ms of stereo samples
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    /// <summary>Idle lead before fallback starts. Larger than real WASAPI burst cadence (~50 ms; loopback delivers in bursts).</summary>
    public static readonly TimeSpan FallbackThreshold = TimeSpan.FromMilliseconds(120);
    private const long MaxCatchUpMs = 1000;

    private readonly IAudioInput _system;
    private readonly IAudioInput? _mic;
    private readonly TimeProvider _time;
    private readonly object _emitLock = new();   // serialises emission so Mixed is ordered
    private readonly object _micLock = new();    // guards the mic queue only (capture thread never waits on Mixed)
    private readonly Queue<float> _micQueue = new();
    private ITimer? _timer;
    private long _timelineEnd;                   // QPC where the next output chunk must start
    private bool _hasTimeline, _filling;
    private volatile bool _micLost, _disposed;
    private int _systemLostNotified, _micLostNotified;

    public AudioMixer(IAudioInput system, IAudioInput? mic, TimeProvider? time = null)
    {
        _system = system;
        _mic = mic;
        _time = time ?? TimeProvider.System;
    }

    public bool MicActive => _mic is not null && !_micLost;
    public bool MuteSystem { get; set; }
    public bool MuteMic { get; set; }
    public event Action<AudioChunk>? Mixed;
    public event Action<string>? Notice;

    private long NowQpc()
    {
        long ts = _time.GetTimestamp(), f = _time.TimestampFrequency, sf = Stopwatch.Frequency;
        return f == sf ? ts : ts / f * sf + ts % f * sf / f;      // integer math: fake clocks start near year 2000 (> 2^53 ticks)
    }

    public void Start()
    {
        _timelineEnd = NowQpc();                 // fallback anchor until the first real chunk
        _system.Data += OnSystemData;
        _system.Lost += OnSystemLost;
        try { _system.Start(); }
        catch
        {
            _system.Data -= OnSystemData;
            _system.Lost -= OnSystemLost;
            throw;                               // nothing else has been started yet
        }
        if (_mic is null) Notice?.Invoke(NoMicMessage);
        else
        {
            _mic.Data += OnMicData; _mic.Lost += OnMicLost;
            try { _mic.Start(); }
            catch
            {
                // Mic failed to start (privacy block, exclusive device, vanished): reported as "not found".
                _mic.Data -= OnMicData; _mic.Lost -= OnMicLost;
                _micLost = true;
                Interlocked.Exchange(ref _micLostNotified, 1);
                Notice?.Invoke(NoMicMessage);
            }
        }
        _timer = _time.CreateTimer(_ => OnTimer(), null, Tick, Tick);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _system.Data -= OnSystemData;
        _system.Lost -= OnSystemLost;
        if (_mic is not null) { _mic.Data -= OnMicData; _mic.Lost -= OnMicLost; }
        (_system as IDisposable)?.Dispose();
        (_mic as IDisposable)?.Dispose();
    }

    private void OnMicData(AudioChunk c)
    {
        if (_micLost || _disposed) return;
        lock (_micLock) foreach (var s in c.Samples) _micQueue.Enqueue(s);
    }

    private void OnMicLost()
    {
        _micLost = true;
        lock (_micLock) _micQueue.Clear();
        if (Interlocked.Exchange(ref _micLostNotified, 1) == 0) Notice?.Invoke(MicLostMessage);
    }

    private void OnSystemLost()
    {
        if (Interlocked.Exchange(ref _systemLostNotified, 1) == 0) Notice?.Invoke(SystemLostMessage);
    }

    private void OnSystemData(AudioChunk c)
    {
        if (_disposed) return;
        lock (_emitLock)
        {
            var samples = c.Samples;
            long qpc = c.QpcTicks;
            if (_hasTimeline)
            {
                long delta = qpc - _timelineEnd;
                if (Math.Abs(delta) <= AudioTiming.ResyncTolerance)
                    qpc = _timelineEnd;                               // jitter: re-stamp contiguously
                else if (delta < 0)
                {
                    // Overlaps audio already emitted (e.g. fallback ran ahead): drop the overlapping leading frames.
                    int skip = (int)Math.Ceiling(-delta * (double)AudioTiming.SampleRate / Stopwatch.Frequency) * 2;
                    if (skip >= samples.Length) return;
                    samples = samples[skip..];
                    qpc = _timelineEnd;
                }
                // else: genuine gap in the capture, keep its timestamp (jump forward)
            }
            _hasTimeline = true;
            _filling = false;
            _timelineEnd = qpc + AudioTiming.Duration(samples.Length);
            Emit(samples, qpc);
        }
    }

    private void OnTimer()
    {
        try
        {
            if (_disposed) return;
            lock (_emitLock)
            {
                long now = NowQpc(), step = AudioTiming.Duration(SamplesPer10Ms);
                long lead = now - _timelineEnd;
                if (!_filling)
                {
                    if (lead <= FallbackThreshold.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond) return;   // audio keeps up
                    _filling = true;
                }
                if (lead > MaxCatchUpMs * Stopwatch.Frequency / 1000)           // long stall: do not flood
                    _timelineEnd = now - Stopwatch.Frequency / 10;
                _hasTimeline = true;
                while (now - _timelineEnd >= step)
                {
                    long qpc = _timelineEnd;
                    _timelineEnd += step;
                    Emit(new float[SamplesPer10Ms], qpc);
                }
            }
        }
        catch { /* a throwing Mixed subscriber must not kill the timer thread */ }
    }

    private void Emit(float[] system, long qpc)
    {
        var mic = TakeMic(system.Length);
        float sg = MuteSystem ? 0f : 1f, mg = MuteMic ? 0f : 1f;
        var outp = new float[system.Length];
        for (int i = 0; i < outp.Length; i++)
            outp[i] = Math.Clamp(system[i] * sg + mic[i] * mg, -1f, 1f);
        Mixed?.Invoke(new AudioChunk(outp, qpc));
    }

    private float[] TakeMic(int n)
    {
        var buf = new float[n];
        lock (_micLock)
        {
            int excess = _micQueue.Count - n - MaxMicBacklog;
            for (excess &= ~1; excess > 0; excess--) _micQueue.Dequeue();   // keep stereo pairs aligned
            for (int i = 0; i < n && _micQueue.Count > 0; i++) buf[i] = _micQueue.Dequeue();
        }
        return buf;
    }
}
