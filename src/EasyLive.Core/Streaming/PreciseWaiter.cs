using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyLive.Core.Streaming;

/// <summary>
/// Sleeps until a QPC deadline with ~1 ms precision (high-resolution waitable timer, Windows 10 1803+; falls back
/// to a normal waitable timer) and wakes early when the stop handle is signalled.
/// </summary>
internal sealed class PreciseWaiter : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    private readonly TimerHandle _timer;
    private readonly WaitHandle _stop;

    public PreciseWaiter(WaitHandle stop)
    {
        _stop = stop;
        var h = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        if (h.IsInvalid) h = CreateWaitableTimerExW(0, null, 0, TimerAllAccess);
        if (h.IsInvalid) throw new InvalidOperationException("Could not create a waitable timer.");
        _timer = new TimerHandle { SafeWaitHandle = h };
    }

    /// <summary>Returns false if the stop handle was signalled.</summary>
    public bool WaitUntil(long dueQpc)
    {
        long remaining = dueQpc - Stopwatch.GetTimestamp();
        if (remaining <= 0) return !_stop.WaitOne(0);
        long due100ns = -(long)(remaining * 10_000_000.0 / Stopwatch.Frequency);   // negative = relative
        if (!SetWaitableTimer(_timer.SafeWaitHandle, ref due100ns, 0, 0, 0, false))
            return !_stop.WaitOne((int)Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
        return WaitHandle.WaitAny(new[] { _stop, _timer }) != 0;
    }

    public void Dispose() => _timer.Dispose();

    private sealed class TimerHandle : WaitHandle { }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, nint completion, nint arg, bool resume);
}
