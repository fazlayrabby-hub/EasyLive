using System.Runtime.InteropServices;

namespace EasyLive.Core.Capture;

internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80;
    public const int DWMWA_CLOAKED = 14;
    public const uint MONITORINFOF_PRIMARY = 1;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprc, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint ctx);
    public static readonly nint DPI_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);

    [DllImport("d3d11.dll")]
    public static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string src, int length, out nint hstring);
    [DllImport("combase.dll")] public static extern int WindowsDeleteString(nint hstring);
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    nint CreateForWindow(nint window, in Guid iid);
    nint CreateForMonitor(nint monitor, in Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    nint GetInterface(in Guid iid);
}

