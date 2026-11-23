namespace EasyLive.Core.Capture;

/// <summary>
/// Decides when a window capture that stopped producing frames should be reported as "minimized".
/// Returns the message once per stall; a newer <c>lastFrame</c> re-arms it.
/// </summary>
public sealed class CaptureStallMonitor
{
    public const string MinimizedMessage = "Your game is minimized — bring it back to continue.";

    private readonly TimeSpan _threshold;
    private TimeSpan? _reportedForFrame;

    public CaptureStallMonitor(TimeSpan threshold) => _threshold = threshold;

    public string? Check(TimeSpan now, TimeSpan lastFrame, bool isIconic)
    {
        if (_reportedForFrame is { } r && lastFrame > r)
            _reportedForFrame = null;                 // a new frame arrived since we reported
        if (!isIconic || now - lastFrame < _threshold || _reportedForFrame is not null)
            return null;
        _reportedForFrame = lastFrame;
        return MinimizedMessage;
    }
}
