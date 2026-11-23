using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace EasyLive.Core.Capture;

/// <summary>Reads a sparse BGRA sample of a captured frame for <see cref="BlankFrameDetector"/>.</summary>
public interface IFrameSampler
{
    /// <summary>Call only inside <see cref="IFrameSource.FrameArrived"/> (the texture is callback-scoped).
    /// Returns BGRA bytes of a few pixels spread over the frame, or null if no sample could be taken.</summary>
    byte[]? Sample(CaptureFrame f);
}

/// <summary>
/// Copies an 8x8 grid of single pixels from the capture texture into a small cached CPU-readable staging texture
/// on the texture's own device, maps it and returns the 64 BGRA pixels (256 bytes). Never throws; null on failure.
/// The Map waits for the GPU copy, so callers sample sparingly (the orchestrator does so at most every 500 ms).
/// </summary>
public sealed unsafe class D3D11FrameSampler : IFrameSampler, IDisposable
{
    public const int Grid = 8;

    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    public byte[]? Sample(CaptureFrame f)
    {
        if (f.Texture == 0 || f.Width <= 0 || f.Height <= 0) return null;
        try
        {
            lock (_gate)
            {
                if (_disposed) return null;
                Marshal.AddRef(f.Texture);
                using var src = new ID3D11Texture2D(f.Texture);   // releases our AddRef
                var desc = src.Description;
                if (desc.Format != Format.B8G8R8A8_UNorm || desc.SampleDescription.Count != 1) return null;
                EnsureDevice(src);

                int w = Math.Min(f.Width, (int)desc.Width), h = Math.Min(f.Height, (int)desc.Height);
                for (int gy = 0; gy < Grid; gy++)
                {
                    int sy = (2 * gy + 1) * h / (2 * Grid);
                    for (int gx = 0; gx < Grid; gx++)
                    {
                        int sx = (2 * gx + 1) * w / (2 * Grid);
                        _context!.CopySubresourceRegion(_staging!, 0, (uint)gx, (uint)gy, 0, src, 0,
                            new Box(sx, sy, 0, sx + 1, sy + 1, 1));
                    }
                }

                var mapped = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var bytes = new byte[Grid * Grid * 4];
                    for (int row = 0; row < Grid; row++)
                        new ReadOnlySpan<byte>((byte*)mapped.DataPointer + row * mapped.RowPitch, Grid * 4)
                            .CopyTo(bytes.AsSpan(row * Grid * 4));
                    return bytes;
                }
                finally { _context.Unmap(_staging!, 0); }
            }
        }
        catch (Exception)
        {
            return null;   // capture callback: never throw; a missing sample just means "not blank"
        }
    }

    private void EnsureDevice(ID3D11Texture2D src)
    {
        // ID3D11DeviceChild::GetDevice (vtable slot 3) returns an AddRef'd pointer we own (see FrameConverter).
        nint dev = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint*, void>)(*(nint**)src.NativePointer)[3])(src.NativePointer, &dev);
        if (_device is not null && _device.NativePointer == dev && _staging is not null)
        {
            Marshal.Release(dev);
            return;
        }
        ReleaseDevice();
        _device = new ID3D11Device(dev);
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);   // the stream session copies on the same device from this thread too
        _context = _device.ImmediateContext;
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = Grid, Height = Grid, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
    }

    private void ReleaseDevice()
    {
        _staging?.Dispose(); _staging = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseDevice();
        }
    }
}
