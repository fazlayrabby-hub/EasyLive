namespace EasyLive.Core;

/// <summary>Everything the window shows about the current stream (raw values; the view model words them).</summary>
public sealed record StreamDetails(
    Platform Platform, string SourceTitle, EncoderKind Encoder, int Width, int Height, int Fps,
    int CurrentKbps, int MinKbps, int MaxKbps, double UploadMbps,
    int DroppedFrames, int SendQueueMs, TimeSpan LiveElapsed)
{
    public int KeyframeSeconds => 2;
}
