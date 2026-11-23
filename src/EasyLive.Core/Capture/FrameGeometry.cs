namespace EasyLive.Core.Capture;

internal static class FrameGeometry
{
    /// <summary>Reported frame size = content size limited to the real texture size, so a consumer's copy region is always valid.</summary>
    public static (int Width, int Height) ClampToTexture(int contentW, int contentH, int texW, int texH)
        => (Math.Min(contentW, texW), Math.Min(contentH, texH));
}
