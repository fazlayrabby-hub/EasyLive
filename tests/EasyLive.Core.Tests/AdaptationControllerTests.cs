using EasyLive.Core;

namespace EasyLive.Core.Tests;

public class AdaptationControllerTests
{
    private static readonly QualityRung Rung = new("1080p30", 1920, 1080, 30, 3000, 4500);

    private static StreamStats Healthy() => new(0, 0, 0, TimeSpan.FromSeconds(1));

    private static StreamStats Congested() => new(0, 2000, 0, TimeSpan.FromSeconds(1));

    [Fact]
    public void Congested_queue_cuts_bitrate_25_percent()
    {
        var c = new AdaptationController(Rung, 4000);
        Assert.Equal(new SetBitrate(3000), c.Decide(Congested()));
    }

    [Fact]
    public void Never_below_rung_min()
    {
        var c = new AdaptationController(Rung, 3200);
        Assert.Equal(new SetBitrate(3000), c.Decide(Congested()));
    }

    [Fact]
    public void Healthy_for_20s_raises_10_percent_capped_at_max()
    {
        var c = new AdaptationController(Rung, 4000);
        for (int i = 0; i < 19; i++) Assert.IsType<Hold>(c.Decide(Healthy()));
        Assert.Equal(new SetBitrate(4400), c.Decide(Healthy()));

        // next raise would be 4840, capped at max 4500
        for (int i = 0; i < 19; i++) Assert.IsType<Hold>(c.Decide(Healthy()));
        Assert.Equal(new SetBitrate(4500), c.Decide(Healthy()));
    }

    [Fact]
    public void Congested_at_floor_for_10s_requests_reinit()
    {
        var c = new AdaptationController(Rung, 3000);
        for (int i = 0; i < 9; i++) Assert.IsType<Hold>(c.Decide(Congested()));
        Assert.IsType<Reinit>(c.Decide(Congested()));
    }

    [Fact]
    public void Single_healthy_second_after_cut_holds()
    {
        var c = new AdaptationController(Rung, 4000);
        Assert.IsType<SetBitrate>(c.Decide(Congested()));
        for (int i = 0; i < 5; i++) Assert.IsType<Hold>(c.Decide(Healthy()));
    }
}
