using System.Diagnostics;

namespace EasyLive.Core.Audio;

/// <summary>Shared constants/helpers for the running QPC audio timeline (48 kHz stereo float).</summary>
internal static class AudioTiming
{
    public const int SampleRate = 48000;

    /// <summary>Largest timestamp mismatch treated as jitter (re-stamped contiguously) rather than a discontinuity.</summary>
    public static readonly long ResyncTolerance = Stopwatch.Frequency / 20;     // 50 ms

    /// <summary>QPC ticks covered by <paramref name="samples"/> interleaved stereo samples.</summary>
    public static long Duration(int samples) =>
        (long)Math.Round(samples / 2 * (double)Stopwatch.Frequency / SampleRate);
}
