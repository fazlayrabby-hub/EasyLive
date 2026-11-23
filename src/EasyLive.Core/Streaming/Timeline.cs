namespace EasyLive.Core.Streaming;

/// <summary>Constant-frame-rate tick schedule anchored at the session start (QPC ticks). Tick n is PTS n in 1/fps.</summary>
internal readonly struct VideoClock
{
    private readonly long _start;
    private readonly long _freq;

    public VideoClock(long startQpc, long qpcFrequency, int fps)
    {
        _start = startQpc;
        _freq = qpcFrequency;
        Fps = fps;
    }

    public int Fps { get; }

    public long TickQpc(long n) => _start + (long)Math.Round(n * (double)_freq / Fps);

    /// <summary>Index of the last tick at or before <paramref name="qpc"/> (0 before the start).</summary>
    public long TickIndexAt(long qpc) => qpc <= _start ? 0 : (long)Math.Floor((qpc - _start) * (double)Fps / _freq + 1e-9);

    /// <summary>Milliseconds since the session start.</summary>
    public long ToMs(long qpc) => (long)Math.Round((qpc - _start) * 1000.0 / _freq);
}

/// <summary>
/// Places contiguous 48 kHz audio chunks on the session timeline (PTS in samples since the session start).
/// Chunks are normally counted back to back; a discrepancy larger than 50 ms between the chunk's QPC time and the
/// running sample count is treated as a real discontinuity: a forward gap is filled with silence, an overlap is
/// dropped. Samples from before the session start are dropped.
/// </summary>
internal sealed class AudioTimeline
{
    private readonly long _start;
    private readonly long _freq;
    private readonly int _rate;
    private readonly long _tolerance;

    public AudioTimeline(long startQpc, long qpcFrequency, int sampleRate = 48000, int toleranceMs = 50)
    {
        _start = startQpc;
        _freq = qpcFrequency;
        _rate = sampleRate;
        _tolerance = (long)sampleRate * toleranceMs / 1000;
    }

    /// <summary>PTS (in samples) the next emitted sample will get.</summary>
    public long NextPts { get; private set; }

    /// <summary>Where a chunk of <paramref name="frames"/> sample frames goes. <c>Pts</c> is the PTS of the first
    /// emitted sample (silence first, if any); skip that many frames from the chunk start.</summary>
    public (long Pts, int SkipFrames, int SilenceFrames) Place(long chunkQpc, int frames)
    {
        long actual = (long)Math.Round((chunkQpc - _start) * (double)_rate / _freq);
        int skip = 0;
        if (actual < 0)
        {
            skip = (int)Math.Min(frames, -actual);
            actual += skip;
        }
        long pts = NextPts;
        if (skip == frames) return (pts, skip, 0);

        int silence = 0;
        long drift = actual - NextPts;
        if (drift > _tolerance) silence = (int)Math.Min(drift, int.MaxValue);
        else if (drift < -_tolerance) skip += (int)Math.Min(frames - skip, -drift);

        NextPts += silence + frames - skip;
        return (pts, skip, silence);
    }
}
