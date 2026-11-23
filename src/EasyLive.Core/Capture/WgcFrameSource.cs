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
    private static readonly Guid IGraphicsCaptureSession3Iid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
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
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session is not null) throw new InvalidOperationException("Already started.");

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
        }
        catch
        {
            Stop();   // outside the lock
            throw;
        }
    }

    public void Stop() => Teardown(dispose: false);

    /// <summary>Detaches everything under the lock, then disposes OUTSIDE it (pool/session Close may wait on in-flight callbacks that take _gate).</summary>
    private void Teardown(bool dispose)
    {
        Timer? timer; GraphicsCaptureItem? item; Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session; IDirect3DDevice? device; ID3D11Device? d3d;
        lock (_gate)
        {
            if (dispose) _disposed = true;
            timer = _stallTimer; _stallTimer = null; _stall = null;
            item = _item; _item = null;
            pool = _pool; _pool = null;
            session = _session; _session = null;
            device = _device; _device = null;
            d3d = _d3d; _d3d = null;
        }
        timer?.Dispose();
        if (item is not null) item.Closed -= OnItemClosed;
        if (pool is not null) pool.FrameArrived -= OnFramePoolFrameArrived;
        session?.Dispose();
        pool?.Dispose();
        (device as IDisposable)?.Dispose();
        d3d?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Teardown(dispose: true);
        return ValueTask.CompletedTask;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        try { Closed?.Invoke("The window you were sharing was closed."); }
        catch (Exception ex) { Debug.WriteLine($"Closed handler threw: {ex}"); }
    }

    private void OnFramePoolFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try { HandleFrame(sender); }
        catch (Exception ex) { Debug.WriteLine($"Frame handling failed: {ex}"); }   // never let it escape a WinRT callback
    }

    private void HandleFrame(Direct3D11CaptureFramePool sender)
    {
        long qpc = Stopwatch.GetTimestamp();
        lock (_gate) { if (_pool != sender) return; }    // stopped

        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Interlocked.Exchange(ref _lastFrameTicks, qpc);

        var size = frame.ContentSize;
        lock (_gate)
        {
            if (_pool != sender) return;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                _poolSize = size;                         // later frames come from the resized pool
                sender.Recreate(_device, PixelFormat, 2, size);
            }
        }

        using var surface = frame.Surface;
        nint surfacePtr = 0, access = 0;
        IDirect3DDxgiInterfaceAccess? acc = null;
        try
        {
            surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            var iid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            if (Marshal.QueryInterface(surfacePtr, in iid, out access) != 0) return;
            acc = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(access);
            nint texture = acc.GetInterface(Texture2DIid);
            using var tex = new ID3D11Texture2D(texture);   // owns the GetInterface reference
            var desc = tex.Description;
            var (w, h) = FrameGeometry.ClampToTexture(size.Width, size.Height, (int)desc.Width, (int)desc.Height);
            FrameArrived?.Invoke(new CaptureFrame(texture, w, h, qpc));
        }
        finally
        {
            if (acc is not null) Marshal.ReleaseComObject(acc);
            if (access != 0) Marshal.Release(access);
            if (surfacePtr != 0) Marshal.Release(surfacePtr);
        }
    }

    /// <summary>Outcome of the last border-off attempt, e.g. "IsBorderRequired=False (readback)" or a failure reason.</summary>
    internal string BorderStatus { get; private set; } = "not attempted";

    /// <summary>Best effort: hide the yellow capture border (Windows 11 22H2+). Non-fatal; result recorded in <see cref="BorderStatus"/>.</summary>
    private unsafe void DisableBorder(GraphicsCaptureSession session)
    {
        // Raw vtable calls: the managed object would be unwrapped by COM interop, and the 19041 SDK lacks the projection.
        // IGraphicsCaptureSession3 vtable: 0-2 IUnknown, 3-5 IInspectable, 6 get_IsBorderRequired, 7 put_IsBorderRequired.
        nint abi = 0, qi = 0;
        try
        {
            abi = ((IWinRTObject)session).NativeObject.ThisPtr;   // borrowed, not AddRef'd
            var iid = IGraphicsCaptureSession3Iid;
            int hr;
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)abi)[0])(abi, &iid, &qi);   // raw QI: Marshal.QueryInterface throws instead of returning the HRESULT
            if (hr != 0) { BorderStatus = $"IGraphicsCaptureSession3 not supported (QI hr=0x{hr:X8})"; return; }
            var vtbl = *(nint**)qi;
            hr = ((delegate* unmanaged[Stdcall]<nint, byte, int>)vtbl[7])(qi, 0);
            if (hr != 0) { BorderStatus = $"put_IsBorderRequired hr=0x{hr:X8}"; return; }
            byte value = 0xFF;
            hr = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)vtbl[6])(qi, &value);
            BorderStatus = hr == 0 ? $"IsBorderRequired={value != 0} (readback)" : $"get_IsBorderRequired hr=0x{hr:X8}";
        }
        catch (Exception ex) { BorderStatus = $"failed: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}"; }
        finally
        {
            if (qi != 0) Marshal.Release(qi);
        }
        Debug.WriteLine($"WGC border: {BorderStatus}");
    }

    private void CheckStall()
    {
        try
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
        catch (Exception ex) { Debug.WriteLine($"Stall check failed: {ex}"); }   // timer thread: must not throw
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
            try
            {
                itemPtr = t.Hwnd != 0
                ? interop.CreateForWindow(t.Hwnd, GraphicsCaptureItemIid)
                : interop.CreateForMonitor(t.Monitor, GraphicsCaptureItemIid);
            }
            finally { Marshal.ReleaseComObject(interop); }
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
