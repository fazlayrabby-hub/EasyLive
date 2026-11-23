namespace EasyLive.Core;

/// <summary>What the view model needs from the Go Live state machine (implemented by <see cref="SessionOrchestrator"/>).</summary>
public interface ISessionController
{
    /// <summary>Raised on any thread.</summary>
    event Action<SessionStatus>? StatusChanged;
    /// <summary>Raised on any thread, about once a second while live.</summary>
    event Action<StreamDetails>? DetailsChanged;
    Task GoLiveAsync(Platform p, EasyLive.Core.Capture.CaptureTarget t, CancellationToken ct);
    Task StopAsync();
}

/// <summary>Runs an action on the UI thread (WPF: Dispatcher.BeginInvoke). Abstracted so the view model needs no UI.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

