using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

public class CaptureStallMonitorTests
{
    private const string Msg = "Your game is minimized — bring it back to continue.";
    private static CaptureStallMonitor Make() => new(TimeSpan.FromSeconds(2));

    [Fact]
    public void Returns_null_before_threshold()
        => Assert.Null(Make().Check(TimeSpan.FromSeconds(1.9), TimeSpan.Zero, isIconic: true));

    [Fact]
    public void Returns_message_at_threshold_when_iconic()
        => Assert.Equal(Msg, Make().Check(TimeSpan.FromSeconds(2), TimeSpan.Zero, isIconic: true));

    [Fact]
    public void Returns_null_when_not_iconic()
        => Assert.Null(Make().Check(TimeSpan.FromSeconds(10), TimeSpan.Zero, isIconic: false));

    [Fact]
    public void Message_only_once_until_new_frame()
    {
        var m = Make();
        Assert.Equal(Msg, m.Check(TimeSpan.FromSeconds(3), TimeSpan.Zero, true));
        Assert.Null(m.Check(TimeSpan.FromSeconds(4), TimeSpan.Zero, true));

        // a new frame arrives (lastFrame advances), later it stalls again
        Assert.Null(m.Check(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4.5), true));
        Assert.Equal(Msg, m.Check(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(4.5), true));
    }
}
