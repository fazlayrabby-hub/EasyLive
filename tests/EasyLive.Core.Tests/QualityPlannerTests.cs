using EasyLive.Core;

namespace EasyLive.Core.Tests;

public class QualityPlannerTests
{
    private static HardwareProfile Hw(params EncoderKind[] encoders) =>
        new("Test GPU", encoders, 8);

    private static NetworkProbeResult Net(double mbps, double loss = 0) =>
        new(mbps, 5, loss, 100);

    [Fact]
    public void Fast_connection_hw_encoder_picks_1080p60()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.QuickSync), Net(20));
        Assert.Equal("1080p60", plan.Rung.Name);
        Assert.Equal(EncoderKind.QuickSync, plan.Encoder);
        Assert.Equal(6000, plan.StartKbps);
        Assert.False(plan.TooSlow);
    }

    [Fact]
    public void Ten_mbps_picks_1080p60_at_start_6000_capped()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.Nvenc), Net(10));
        Assert.Equal("1080p60", plan.Rung.Name);
        Assert.Equal(6000, plan.StartKbps);
    }

    [Fact]
    public void Five_mbps_picks_1080p30()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.Amf), Net(5));
        Assert.Equal("1080p30", plan.Rung.Name);
        Assert.Equal(3000, plan.StartKbps);
    }

    [Fact]
    public void Two_mbps_picks_720p30()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.Amf), Net(2.6));
        Assert.Equal("720p30", plan.Rung.Name);
        Assert.Equal(1560, plan.StartKbps);
    }

    [Fact]
    public void Very_slow_connection_is_TooSlow()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.Amf), Net(1.0));
        Assert.True(plan.TooSlow);
        Assert.Equal("480p30", plan.Rung.Name);
    }

    [Fact]
    public void Software_only_is_capped_at_720p30()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.OpenH264), Net(50));
        Assert.Equal("720p30", plan.Rung.Name);
        Assert.Equal(EncoderKind.OpenH264, plan.Encoder);
    }

    [Fact]
    public void High_loss_drops_one_rung()
    {
        var plan = QualityPlanner.Choose(Hw(EncoderKind.Nvenc), Net(20, loss: 3));
        Assert.Equal("1080p30", plan.Rung.Name);
    }
}
