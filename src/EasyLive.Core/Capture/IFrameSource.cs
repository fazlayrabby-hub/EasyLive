namespace EasyLive.Core.Capture;

/// <summary>
/// One captured frame. <see cref="Texture"/> is a native ID3D11Texture2D pointer that is valid ONLY for the
/// duration of the <see cref="IFrameSource.FrameArrived"/> callback (the underlying capture surface is returned to
/// the pool afterwards). A consumer that needs it later must copy it into its own texture (or AddRef it) before
/// returning from the handler. Do not Release the pointer unless you AddRef'd it.
/// </summary>
/// <param name="QpcTicks">Arrival time from <c>Stopwatch.GetTimestamp()</c> (QueryPerformanceCounter ticks).</param>
public sealed record CaptureFrame(nint Texture, int Width, int Height, long QpcTicks);

/// <summary>A monitor (Monitor != 0, Hwnd == 0) or a top-level window (Hwnd != 0, Monitor == 0).</summary>
public sealed record CaptureTarget(string Title, nint Hwnd, nint Monitor);

public interface IFrameSource : IAsyncDisposable
{
    /// <summary>Raised on a capture thread for every frame. See <see cref="CaptureFrame"/> for texture lifetime.</summary>
    event Action<CaptureFrame>? FrameArrived;

    /// <summary>Plain-words reason the source can no longer deliver (closed) or needs attention (minimized).</summary>
    event Action<string>? Closed;

    void Start(CaptureTarget target);
    void Stop();
}
