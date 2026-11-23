using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

/// <summary>In-memory IFrameSource for tests of downstream components.</summary>
public sealed class FakeFrameSource : IFrameSource
{
    public event Action<CaptureFrame>? FrameArrived;
    public event Action<string>? Closed;

    public CaptureTarget? Target { get; private set; }
    public bool Running { get; private set; }

    public void Start(CaptureTarget target) { Target = target; Running = true; }
    public void Stop() => Running = false;
    public void Raise(CaptureFrame frame) => FrameArrived?.Invoke(frame);
    public void RaiseClosed(string reason) => Closed?.Invoke(reason);
    public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
}
