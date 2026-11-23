using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

public class BlankFrameDetectorTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(3);

    private static byte[] Pixels(byte b, byte g, byte r, byte a = 255, int count = 16)
    {
        var buf = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            buf[i * 4] = b; buf[i * 4 + 1] = g; buf[i * 4 + 2] = r; buf[i * 4 + 3] = a;
        }
        return buf;
    }

    private static byte[] Black() => Pixels(0, 0, 0);
    private static byte[] Gray() => Pixels(128, 128, 128);
    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Black_for_2_9s_is_false()
    {
        var d = new BlankFrameDetector(Threshold);
        Assert.False(d.Observe(Black(), S(0)));
        Assert.False(d.Observe(Black(), S(2.9)));
    }

    [Fact]
    public void Black_for_3_1s_is_true()
    {
        var d = new BlankFrameDetector(Threshold);
        Assert.False(d.Observe(Black(), S(0)));
        Assert.True(d.Observe(Black(), S(3.1)));
    }

    [Fact]
    public void Exact_boundary_3s_is_true()
    {
        var d = new BlankFrameDetector(Threshold);
        d.Observe(Black(), S(10));
        Assert.True(d.Observe(Black(), S(13)));
    }

    [Fact]
    public void Stays_true_until_non_blank()
    {
        var d = new BlankFrameDetector(Threshold);
        d.Observe(Black(), S(0));
        Assert.True(d.Observe(Black(), S(3.5)));
        Assert.True(d.Observe(Black(), S(4)));
        Assert.False(d.Observe(Gray(), S(4.5)));
        Assert.False(d.Observe(Black(), S(5)));
    }

    [Fact]
    public void Non_black_frame_resets_timer()
    {
        var d = new BlankFrameDetector(Threshold);
        d.Observe(Black(), S(0));
        d.Observe(Black(), S(2));
        Assert.False(d.Observe(Gray(), S(2.5)));
        d.Observe(Black(), S(3));
        Assert.False(d.Observe(Black(), S(5.9)));
        Assert.True(d.Observe(Black(), S(6)));
    }

    [Fact]
    public void One_non_black_pixel_makes_frame_non_blank()
    {
        var d = new BlankFrameDetector(Threshold);
        var frame = Black();
        frame[8 * 4 + 1] = 255; // green of pixel 8
        d.Observe(frame, S(0));
        Assert.False(d.Observe(frame, S(10)));
    }

    [Fact]
    public void Pure_blue_in_BGRA_is_not_blank()
    {
        var d = new BlankFrameDetector(Threshold);
        var blue = Pixels(b: 255, g: 0, r: 0); // luma ~29
        d.Observe(blue, S(0));
        Assert.False(d.Observe(blue, S(10)));
    }

    [Fact]
    public void Pure_green_is_not_blank()
    {
        var d = new BlankFrameDetector(Threshold);
        var green = Pixels(b: 0, g: 255, r: 0);
        d.Observe(green, S(0));
        Assert.False(d.Observe(green, S(10)));
    }

    [Fact]
    public void Near_black_below_luma_8_is_blank()
    {
        var d = new BlankFrameDetector(Threshold);
        var dark = Pixels(b: 7, g: 7, r: 7);
        d.Observe(dark, S(0));
        Assert.True(d.Observe(dark, S(3)));
    }

    [Fact]
    public void Alpha_is_ignored()
    {
        var d = new BlankFrameDetector(Threshold);
        var f = Pixels(0, 0, 0, a: 255);
        d.Observe(f, S(0));
        Assert.True(d.Observe(f, S(3.1)));
    }

    [Fact]
    public void Empty_sample_is_not_blank_and_resets()
    {
        var d = new BlankFrameDetector(Threshold);
        d.Observe(Black(), S(0));
        Assert.False(d.Observe(ReadOnlySpan<byte>.Empty, S(2)));
        d.Observe(Black(), S(2.5));
        Assert.False(d.Observe(Black(), S(5)));
        Assert.True(d.Observe(Black(), S(5.5)));
    }

    [Fact]
    public void Backwards_timestamp_resets_timer()
    {
        var d = new BlankFrameDetector(Threshold);
        d.Observe(Black(), S(10));
        Assert.False(d.Observe(Black(), S(5)));
        Assert.False(d.Observe(Black(), S(7.9)));
        Assert.True(d.Observe(Black(), S(8)));
    }

    [Fact]
    public void Frozen_identical_non_black_frames_never_true()
    {
        var d = new BlankFrameDetector(Threshold);
        var g = Gray();
        for (var t = 0; t <= 60; t++)
            Assert.False(d.Observe(g, S(t)));
    }
}
