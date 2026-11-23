using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

public class FakeFrameSourceTests
{
    [Fact]
    public void Raises_frame_and_closed_events()
    {
        var f = new FakeFrameSource();
        CaptureFrame? got = null; string? why = null;
        f.FrameArrived += x => got = x;
        f.Closed += r => why = r;
        f.Raise(new CaptureFrame(1, 1920, 1080, 42));
        f.RaiseClosed("gone");
        Assert.Equal(new CaptureFrame(1, 1920, 1080, 42), got);
        Assert.Equal("gone", why);
    }
}
