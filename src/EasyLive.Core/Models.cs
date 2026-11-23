namespace EasyLive.Core;

public enum EncoderKind { QuickSync, Amf, Nvenc, OpenH264 }

public sealed record HardwareProfile(string GpuName, IReadOnlyList<EncoderKind> Encoders, int CpuLogicalCores);

public sealed record NetworkProbeResult(double UploadMbps, double JitterMs, double LossPct, double ConnectMs);

public sealed record QualityRung(string Name, int Width, int Height, int Fps, int MinKbps, int MaxKbps);

public sealed record StreamPlan(EncoderKind Encoder, QualityRung Rung, int StartKbps, bool TooSlow);
