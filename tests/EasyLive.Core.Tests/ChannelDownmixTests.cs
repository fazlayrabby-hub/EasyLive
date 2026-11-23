using EasyLive.Core.Audio;

namespace EasyLive.Core.Tests;

public class ChannelDownmixTests
{
    private const float H = 0.70710678f;
    private const float K = H / (1 + 2 * H);     // 5.1: one 0.707 contribution each side plus the centre

    [Fact]
    public void Stereo_is_passthrough()
    {
        var s = new[] { 0.1f, -0.2f, 0.3f, 0.4f };
        Assert.Equal(s, ChannelDownmix.ToStereo(s, 2));
    }

    [Fact]
    public void Mono_is_duplicated_to_both_channels()
        => Assert.Equal(new[] { 0.5f, 0.5f, -0.25f, -0.25f }, ChannelDownmix.ToStereo(new[] { 0.5f, -0.25f }, 1));

    [Fact]
    public void Surround_51_front_left_only()
    {
        var o = ChannelDownmix.ToStereo(new[] { 1f, 0, 0, 0, 0, 0 }, 6);    // FL FR FC LFE BL BR
        Assert.Equal(1 / (1 + 2 * H), o[0], 4);
        Assert.Equal(0f, o[1], 4);
    }

    [Fact]
    public void Surround_51_centre_goes_to_both_sides()
    {
        var o = ChannelDownmix.ToStereo(new[] { 0, 0, 1f, 0, 0, 0 }, 6);
        Assert.Equal(K, o[0], 4);
        Assert.Equal(K, o[1], 4);
    }

    [Fact]
    public void Surround_51_lfe_ignored_and_rears_go_to_their_side()
    {
        var o = ChannelDownmix.ToStereo(new[] { 0, 0, 0, 1f, 1f, 0 }, 6);   // LFE + BL
        Assert.Equal(K, o[0], 4);
        Assert.Equal(0f, o[1], 4);
        o = ChannelDownmix.ToStereo(new[] { 0, 0, 0, 0, 0, 1f }, 6);        // BR
        Assert.Equal(0f, o[0], 4);
        Assert.Equal(K, o[1], 4);
    }

    [Fact]
    public void Surround_all_ones_does_not_clip_and_handles_multiple_frames()
    {
        var o = ChannelDownmix.ToStereo(Enumerable.Repeat(1f, 12).ToArray(), 6);
        Assert.Equal(4, o.Length);
        Assert.All(o, v => Assert.InRange(v, 0f, 1.0001f));
    }
}
