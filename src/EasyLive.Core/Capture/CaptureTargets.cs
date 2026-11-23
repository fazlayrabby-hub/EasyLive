using static EasyLive.Core.Capture.NativeMethods;

namespace EasyLive.Core.Capture;

/// <summary>Enumerates what can be captured: monitors and visible, normal top-level windows.</summary>
public static class CaptureTargets
{
    public static IReadOnlyList<CaptureTarget> List()
    {
        // Monitor rects must be physical pixels (what WGC delivers), regardless of the host process DPI mode.
        nint prevDpi = SetThreadDpiAwarenessContext(DPI_PER_MONITOR_AWARE_V2);
        try { return Enumerate(); }
        finally { if (prevDpi != 0) SetThreadDpiAwarenessContext(prevDpi); }
    }

    private static List<CaptureTarget> Enumerate()
    {
        var result = new List<CaptureTarget>();
        int index = 0;

        EnumDisplayMonitors(0, 0, (hMon, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(hMon, ref info))
            {
                index++;
                int w = info.rcMonitor.Right - info.rcMonitor.Left, h = info.rcMonitor.Bottom - info.rcMonitor.Top;
                bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                result.Add(new CaptureTarget($"Monitor {index} ({w}x{h})" + (primary ? " - primary" : ""), 0, hMon));
            }
            return true;
        }, 0);

        uint self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (IsCapturableWindow(hwnd, self, out var title))
                result.Add(new CaptureTarget(title, hwnd, 0));
            return true;
        }, 0);

        return result;
    }

    private static bool IsCapturableWindow(nint hwnd, uint selfPid, out string title)
    {
        title = "";
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == selfPid) return false;
        int len = GetWindowTextLengthW(hwnd);
        if (len <= 0) return false;
        var buf = new char[len + 1];
        int n = GetWindowTextW(hwnd, buf, buf.Length);
        if (n <= 0) return false;
        title = new string(buf, 0, n);
        return !string.IsNullOrWhiteSpace(title);
    }
}
