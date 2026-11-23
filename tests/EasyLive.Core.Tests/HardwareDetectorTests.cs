using EasyLive.Core;
using Xunit.Abstractions;

namespace EasyLive.Core.Tests;

public class HardwareDetectorTests
{
    private readonly ITestOutputHelper _out;
    public HardwareDetectorTests(ITestOutputHelper output) => _out = output;

    private static void AssertEncoders(HardwareProfile p, params EncoderKind[] expected) =>
        Assert.Equal(expected, p.Encoders.ToArray());

    [Fact]
    public void Intel_gives_quicksync()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Intel(R) Iris(R) Xe Graphics" }, 8, null);
        AssertEncoders(p, EncoderKind.QuickSync, EncoderKind.OpenH264);
        Assert.Equal("Intel(R) Iris(R) Xe Graphics", p.GpuName);
        Assert.Equal(8, p.CpuLogicalCores);
    }

    [Fact]
    public void Amd_gives_amf() =>
        AssertEncoders(HardwareDetector.FromAdapters(new[] { "AMD Radeon RX 6600" }, 8, null),
            EncoderKind.Amf, EncoderKind.OpenH264);

    [Fact]
    public void Nvidia_gives_nvenc() =>
        AssertEncoders(HardwareDetector.FromAdapters(new[] { "NVIDIA GeForce RTX 3060" }, 8, null),
            EncoderKind.Nvenc, EncoderKind.OpenH264);

    [Fact]
    public void Hybrid_uses_capturing_adapter()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Intel UHD", "NVIDIA GTX 1650" }, 8, 1);
        AssertEncoders(p, EncoderKind.Nvenc, EncoderKind.OpenH264);
        Assert.Equal("NVIDIA GTX 1650", p.GpuName);
    }

    [Fact]
    public void Hybrid_capturing_intel_overrides_dgpu_preference()
    {
        var p = HardwareDetector.FromAdapters(new[] { "NVIDIA GTX 1650", "Intel UHD" }, 8, 1);
        AssertEncoders(p, EncoderKind.QuickSync, EncoderKind.OpenH264);
    }

    [Fact]
    public void Hybrid_without_capturing_prefers_dgpu()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Intel UHD", "NVIDIA GTX 1650" }, 8, null);
        AssertEncoders(p, EncoderKind.Nvenc, EncoderKind.OpenH264);
    }

    [Fact]
    public void Invalid_capturing_index_falls_back_to_preference()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Intel UHD", "AMD Radeon RX 6600" }, 8, 5);
        AssertEncoders(p, EncoderKind.Amf, EncoderKind.OpenH264);
    }

    [Fact]
    public void Unknown_vendor_gives_openh264_only() =>
        AssertEncoders(HardwareDetector.FromAdapters(new[] { "Matrox" }, 4, null), EncoderKind.OpenH264);

    [Fact]
    public void Basic_render_driver_gives_openh264_only()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Microsoft Basic Render Driver" }, 4, null);
        AssertEncoders(p, EncoderKind.OpenH264);
        Assert.Equal("Microsoft Basic Render Driver", p.GpuName);
    }

    [Fact]
    public void Basic_render_is_skipped_when_real_gpu_exists()
    {
        var p = HardwareDetector.FromAdapters(new[] { "Microsoft Basic Render Driver", "Intel UHD" }, 4, null);
        AssertEncoders(p, EncoderKind.QuickSync, EncoderKind.OpenH264);
        Assert.Equal("Intel UHD", p.GpuName);
    }

    [Fact]
    public void Empty_list_is_unknown()
    {
        var p = HardwareDetector.FromAdapters(Array.Empty<string>(), 2, null);
        Assert.Equal("Unknown", p.GpuName);
        AssertEncoders(p, EncoderKind.OpenH264);
    }

    [Fact]
    public void Detect_uses_source()
    {
        var p = HardwareDetector.Detect(new FakeSource(), null);
        Assert.Equal(6, p.CpuLogicalCores);
        AssertEncoders(p, EncoderKind.Amf, EncoderKind.OpenH264);
    }

    private sealed class FakeSource : IAdapterSource
    {
        public IReadOnlyList<string> AdapterNames() => new[] { "AMD Radeon RX 6600" };
        public int CpuCores() => 6;
    }

    [Fact(Skip = "manual")]
    public void Manual_smoke_prints_real_hardware()
    {
        var src = new DxgiAdapterSource();
        var p = HardwareDetector.Detect(src);
        _out.WriteLine($"Adapters: {string.Join(" | ", src.AdapterNames())}");
        _out.WriteLine($"GPU={p.GpuName} Encoders=[{string.Join(", ", p.Encoders)}] Cores={p.CpuLogicalCores}");
    }
}
