using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using static EasyLive.Core.Capture.NativeMethods;

namespace EasyLive.Core.Capture;

/// <summary>Windows.Graphics.Capture implementation of <see cref="IFrameSource"/> (GPU frames, free-threaded pool).</summary>
public sealed class WgcFrameSource : IFrameSource
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private ID3D11Device? _d3d;
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Timer? _stallTimer;
    private CaptureStallMonitor? _stall;
    private SizeInt32 _poolSize;
    private nint _hwnd;
    private long _lastFrameTicks;
    private bool _disposed;

    public event Action<CaptureFrame>? FrameArrived;
    public event Action<string>? Closed;

    public void Start(CaptureTarget target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) throw new InvalidOperationException("Already started.");

            try
            {
                _item = CreateItem(target);
                _d3d = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                _device = CreateWinRtDevice(_d3d);
                _poolSize = _item.Size;
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, PixelFormat, 2, _poolSize);
                _pool.FrameArrived += OnFramePoolFrameArrived;
                _item.Closed += OnItemClosed;
                _session = _pool.CreateCaptureSession(_item);
                _session.IsCursorCaptureEnabled = true;
                DisableBorder(_session);

                _hwnd = target.Hwnd;
                _lastFrameTicks = Stopwatch.GetTimestamp();
                if (_hwnd != 0)
                {
                    _stall = new CaptureStallMonitor(StallThreshold);
                    _stallTimer = new Timer(_ => CheckStall(), null, 500, 500);
                }
                _session.StartCapture();
            }
            catch
            {
                StopLocked();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _stallTimer?.Dispose(); _stallTimer = null; _stall = null;
        if (_item is not null) _item.Closed -= OnItemClosed;
        if (_pool is not null) _pool.FrameArrived -= OnFramePoolFrameArrived;
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        _item = null;
        (_device as IDisposable)?.Dispose(); _device = null;
        _d3d?.Dispose(); _d3d = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
        return ValueTask.CompletedTask;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
        => Closed?.Invoke("The window you were sharing was closed.");

    private void OnFramePoolFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        long qpc = Stopwatch.GetTimestamp();
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Interlocked.Exchange(ref _lastFrameTicks, qpc);

        var size = frame.ContentSize;
        lock (_gate)
        {
            if (_pool != sender) return;                 // stopped meanwhile
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;
                sender.Recreate(_device, PixelFormat, 2, size);
            }
        }

        nint surfacePtr = 0, access = 0, texture = 0;
        try
        {
            surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
            var iid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            if (Marshal.QueryInterface(surfacePtr, in iid, out access) != 0) return;
            var acc = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(access);
            texture = acc.GetInterface(Texture2DIid);
            FrameArrived?.Invoke(new CaptureFrame(texture, size.Width, size.Height, qpc));
        }
        finally
        {
            if (texture != 0) Marshal.Release(texture);
            if (access != 0) Marshal.Release(access);
            if (surfacePtr != 0) Marshal.Release(surfacePtr);
        }
    }

    /// <summary>Best effort: hide the yellow capture border (Windows 11 22H2+); silently ignored elsewhere.</summary>
    private static void DisableBorder(GraphicsCaptureSession session)
    {
        nint abi = 0, qi = 0;
        try
        {
            abi = MarshalInterface<GraphicsCaptureSession>.FromManaged(session);
            var iid = typeof(IGraphicsCaptureSession3).GUID;
            if (Marshal.QueryInterface(abi, in iid, out qi) == 0)
                ((IGraphicsCaptureSession3)Marshal.GetObjectForIUnknown(qi)).put_IsBorderRequired(false);
        }
        catch { }
        finally
        {
            if (qi != 0) Marshal.Release(qi);
            if (abi != 0) Marshal.Release(abi);
        }
    }

    private void CheckStall()
    {
        CaptureStallMonitor? monitor;
        nint hwnd;
        lock (_gate) { monitor = _stall; hwnd = _hwnd; }
        if (monitor is null) return;
        var now = TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var last = TimeSpan.FromSeconds(Interlocked.Read(ref _lastFrameTicks) / (double)Stopwatch.Frequency);
        var msg = monitor.Check(now, last, IsIconic(hwnd));
        if (msg is not null) Closed?.Invoke(msg);
    }

    private static GraphicsCaptureItem CreateItem(CaptureTarget t)
    {
        nint hs = 0, factory = 0, itemPtr = 0;
        try
        {
            const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out hs));
            var interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, in interopIid, out factory));
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            itemPtr = t.Hwnd != 0
                ? interop.CreateForWindow(t.Hwnd, GraphicsCaptureItemIid)
                : interop.CreateForMonitor(t.Monitor, GraphicsCaptureItemIid);
            return GraphicsCaptureItem.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != 0) Marshal.Release(itemPtr);
            if (factory != 0) Marshal.Release(factory);
            if (hs != 0) WindowsDeleteString(hs);
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device d3d)
    {
        using var dxgi = d3d.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
}
