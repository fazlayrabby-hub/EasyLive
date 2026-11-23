using Vortice.DXGI;

namespace EasyLive.Core;

public interface IAdapterSource
{
    IReadOnlyList<string> AdapterNames();
    int CpuCores();
}

/// <summary>Enumerates GPU adapters through DXGI (Vortice.DXGI, MIT).</summary>
public sealed class DxgiAdapterSource : IAdapterSource
{
    public IReadOnlyList<string> AdapterNames()
    {
        var names = new List<string>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
            {
                using (adapter)
                    names.Add(adapter.Description1.Description);
            }
        }
        catch (Exception)
        {
            // DXGI unavailable: report no adapters so the caller falls back to software encoding.
        }
        return names;
    }

    public int CpuCores() => Environment.ProcessorCount;
}

public static class HardwareDetector
{
    public static HardwareProfile Detect(IAdapterSource src, int? capturingAdapterIndex = null) =>
        FromAdapters(src.AdapterNames(), src.CpuCores(), capturingAdapterIndex);

    public static HardwareProfile FromAdapters(IReadOnlyList<string> names, int cores, int? capturing)
    {
        if (names.Count == 0)
            return new HardwareProfile("Unknown", new[] { EncoderKind.OpenH264 }, cores);

        string chosen;
        if (capturing is int idx && idx >= 0 && idx < names.Count)
            chosen = names[idx];
        else
        {
            var real = names.Where(n => !IsBasicRender(n)).ToList();
            if (real.Count == 0) chosen = names[0];
            else chosen = real.FirstOrDefault(IsDiscrete) ?? real[0];
        }

        var encoders = new List<EncoderKind>();
        if (!IsBasicRender(chosen))
        {
            if (Has(chosen, "NVIDIA") || Has(chosen, "GeForce")) encoders.Add(EncoderKind.Nvenc);
            else if (Has(chosen, "AMD") || Has(chosen, "Radeon")) encoders.Add(EncoderKind.Amf);
            else if (Has(chosen, "Intel")) encoders.Add(EncoderKind.QuickSync);
        }
        encoders.Add(EncoderKind.OpenH264);
        return new HardwareProfile(chosen, encoders, cores);
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
    private static bool IsBasicRender(string n) => Has(n, "Basic Render");
    private static bool IsDiscrete(string n) => Has(n, "NVIDIA") || Has(n, "GeForce") || Has(n, "AMD") || Has(n, "Radeon");
}
