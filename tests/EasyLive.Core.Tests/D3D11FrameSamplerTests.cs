using EasyLive.Core.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EasyLive.Core.Tests;

public class D3D11FrameSamplerTests
{
    private static ID3D11Device CreateDevice()
    {
        try { return D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport); }
        catch { return D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport); }
    }

    private static unsafe ID3D11Texture2D CreateTexture(ID3D11Device device, int w, int h, Func<int, int, uint> bgra)
    {
        var pixels = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                pixels[y * w + x] = bgra(x, y);
        fixed (uint* p = pixels)
        {
            return device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }, new SubresourceData((nint)p, (uint)(w * 4)));
        }
    }

    [Fact]
    public void Samples_an_8x8_grid_of_bgra_pixels()
    {
        using var device = CreateDevice();
        // left half black, right half white (BGRA little-endian: 0xAARRGGBB)
        using var tex = CreateTexture(device, 640, 360, (x, _) => x < 320 ? 0xFF000000u : 0xFFFFFFFFu);
        using var sampler = new D3D11FrameSampler();

        var bytes = sampler.Sample(new CaptureFrame(tex.NativePointer, 640, 360, 0));

        Assert.NotNull(bytes);
        Assert.Equal(8 * 8 * 4, bytes!.Length);
        for (int gy = 0; gy < 8; gy++)
            for (int gx = 0; gx < 8; gx++)
            {
                byte expected = gx < 4 ? (byte)0 : (byte)255;
                int i = (gy * 8 + gx) * 4;
                Assert.Equal(expected, bytes[i]);
                Assert.Equal(expected, bytes[i + 1]);
                Assert.Equal(expected, bytes[i + 2]);
            }

        // second call reuses the cached staging texture and sees new content
        using var black = CreateTexture(device, 640, 360, (_, _) => 0xFF000000u);
        var again = sampler.Sample(new CaptureFrame(black.NativePointer, 640, 360, 0));
        Assert.NotNull(again);
        Assert.True(new BlankFrameDetector(TimeSpan.Zero).Observe(again, TimeSpan.Zero));
    }

    [Fact]
    public void Returns_null_for_unusable_frames_and_after_dispose()
    {
        var sampler = new D3D11FrameSampler();
        Assert.Null(sampler.Sample(new CaptureFrame(0, 640, 360, 0)));
        using var device = CreateDevice();
        using var tex = CreateTexture(device, 64, 64, (_, _) => 0xFF808080u);
        sampler.Dispose();
        Assert.Null(sampler.Sample(new CaptureFrame(tex.NativePointer, 64, 64, 0)));
    }
}
