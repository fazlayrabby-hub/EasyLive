namespace EasyLive.Core;

public static class QualityPlanner
{
    private const double BudgetFraction = 0.6;
    private const double LossStepDownPct = 2;

    public static IReadOnlyList<QualityRung> Ladder { get; } = new[]
    {
        new QualityRung("1080p60", 1920, 1080, 60, 4500, 6000),
        new QualityRung("1080p30", 1920, 1080, 30, 3000, 4500),
        new QualityRung("720p60", 1280, 720, 60, 2500, 4500),
        new QualityRung("720p30", 1280, 720, 30, 1500, 2500),
        new QualityRung("480p30", 854, 480, 30, 700, 1000),
    };

    public static StreamPlan Choose(HardwareProfile hw, NetworkProbeResult net)
    {
        var encoder = hw.Encoders.Count > 0 ? hw.Encoders[0] : EncoderKind.OpenH264;
        double budget = BudgetFraction * net.UploadMbps * 1000;

        var allowed = encoder == EncoderKind.OpenH264
            ? Ladder.Where(r => r.Name is "720p30" or "480p30").ToList()
            : Ladder.ToList();

        int index = allowed.FindIndex(r => r.MinKbps <= budget);
        if (index < 0) index = allowed.Count - 1;

        if (net.LossPct >= LossStepDownPct)
            index = Math.Min(index + 1, allowed.Count - 1);

        var rung = allowed[index];
        var lowest = allowed[^1];
        bool tooSlow = budget < lowest.MinKbps;
        int startKbps = (int)Math.Min(budget, rung.MaxKbps);
        return new StreamPlan(encoder, rung, startKbps, tooSlow);
    }
}
