using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

public class FrameGeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 1920, 1080)]
    [InlineData(2000, 1200, 1920, 1080, 1920, 1080)] // window grew: texture still old size
    [InlineData(800, 600, 1920, 1080, 800, 600)]     // window shrank: content smaller than texture
    [InlineData(2000, 500, 1920, 1080, 1920, 500)]
    public void ClampToTexture_never_exceeds_texture(int cw, int ch, int tw, int th, int ew, int eh)
        => Assert.Equal((ew, eh), FrameGeometry.ClampToTexture(cw, ch, tw, th));
}
