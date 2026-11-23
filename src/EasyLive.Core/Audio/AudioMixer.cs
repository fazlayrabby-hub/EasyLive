using System.Diagnostics;

namespace EasyLive.Core.Audio;

/// <summary>
/// Mixes game sound (the clock anchor) with an optional microphone. Each system chunk pulls the same
/// number of samples from the mic buffer (silence-padded). If the system input is idle, a 10 ms timer
/// keeps the audio clock running by emitting silence plus mic.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const string NoMicMessage = "Microphone not found — streaming game sound only.";
    public const string MicLostMessage = "Microphone disconnected — streaming game sound only.";
    public const string SystemLostMessage = "Game sound capture stopped.";

    private const int SamplesPer10Ms = 960;                 // 480 frames x 2 channels
    private const int MaxMicBacklog = 19200;                // 200 ms of stereo samples
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(10);

    private readonly IAudioInput _system;
    private readonly IAudioInput? _mic;
    private readonly TimeProvider _time;
    private readonly object _emitLock = new();   // serialises emission so Mixed is ordered
    private readonly object _micLock = new();    // guards the mic queue only (capture thread never waits on Mixed)
    private readonly Queue<float> _micQueue = new();
    private ITimer? _timer;
    private long _lastSystemTs, _fallbackClock;   // _fallbackClock = start of the next fallback chunk
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

    public void Start()
    {
        _lastSystemTs = _time.GetTimestamp();
        _fallbackClock = _lastSystemTs + _time.TimestampFrequency / 100;
        _system.Data += OnSystemData;
        _system.Lost += OnSystemLost;
        if (_mic is null) Notice?.Invoke(NoMicMessage);
        else { _mic.Data += OnMicData; _mic.Lost += OnMicLost; }
        _system.Start();
        _mic?.Start();
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
            _lastSystemTs = _time.GetTimestamp();
            _fallbackClock = _lastSystemTs + _time.TimestampFrequency / 100;
            Emit(c.Samples, c.QpcTicks);
        }
    }

    private void OnTimer()
    {
        try
        {
            if (_disposed) return;
            lock (_emitLock)
            {
                long freq = _time.TimestampFrequency, now = _time.GetTimestamp(), step = freq / 100;
                if (now - _lastSystemTs < freq / 50) return;                          // system still flowing
                if (now - _fallbackClock > 20 * step) _fallbackClock = now - 20 * step;   // after a long stall, don't flood
                // Timers fire late (~15 ms granularity on Windows): catch up so the audio clock stays real-time.
                while (now - _fallbackClock >= step)
                {
                    long qpc = (long)(_fallbackClock * (double)Stopwatch.Frequency / freq);
                    _fallbackClock += step;
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
