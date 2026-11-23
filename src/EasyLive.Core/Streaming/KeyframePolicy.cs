namespace EasyLive.Core.Streaming;

/// <summary>
/// Keyframes by stream TIME, not frame count: platforms (Facebook) require one at least every 2 s, and a frame-count
/// GOP stretches whenever ticks are skipped or the encoder is re-opened. The encoder's own gop_size (2 × fps) stays as a
/// backstop; this forces an IDR as soon as 2 s of stream time passed since the last keyframe (forced or encoder-made).
/// </summary>
internal static class KeyframePolicy
{
    public const int IntervalMs = 2000;

    /// <summary>"No keyframe yet" value for <c>lastKeyPtsMs</c>.</summary>
    public const long None = long.MinValue / 4;

    public static bool ShouldForce(long ptsMs, long lastKeyPtsMs, int intervalMs = IntervalMs) =>
        ptsMs - lastKeyPtsMs >= intervalMs;
}
