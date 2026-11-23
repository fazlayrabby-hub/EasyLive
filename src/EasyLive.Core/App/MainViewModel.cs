using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EasyLive.Core.Capture;
using EasyLive.Core.Streaming;

namespace EasyLive.Core;

/// <summary>State and commands of the single EasyLive window. Needs no UI thread: all UI work goes through <see cref="IUiDispatcher"/>.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public const string IdleHint = "Pick what to share, paste your stream key, then press Go Live.";
    public const string FacebookKeyRejected =
        "Facebook did not accept the stream key. Facebook keys work only once, so paste the new key from Facebook Live Producer, then press Go Live.";

    private readonly ISessionController _session;
    private readonly KeyStore _keys;
    private readonly IUiDispatcher _ui;
    private readonly Func<IReadOnlyList<CaptureTarget>> _listTargets;
    private readonly RelayCommand _goLive, _stop;

    private CaptureTarget? _selected;
    private Platform _platform = Platform.YouTube;
    private string _streamKey = "";
    private string _status = IdleHint;
    private string _qualityText = "";
    private SessionState _state = SessionState.Idle;
    private bool _micMuted, _systemMuted;

    // The orchestrator reads the key from the KeyStore, so it is saved when Go Live is pressed and rolled back if the stream never went live.
    private Platform? _runPlatform;
    private string? _previousKey;
    private bool _runWentLive;
    private CancellationTokenSource? _runCts;

    public MainViewModel(ISessionController session, KeyStore keys, IUiDispatcher ui,
                         Func<IReadOnlyList<CaptureTarget>> listTargets)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _listTargets = listTargets ?? throw new ArgumentNullException(nameof(listTargets));

        _goLive = new RelayCommand(GoLive, () => CanGoLive);
        _stop = new RelayCommand(() => _ = StopSafe(), () => IsBusy);
        GoLiveCommand = _goLive;
        StopCommand = _stop;
        MuteMicCommand = new RelayCommand(() => { MicMuted = !MicMuted; MuteChanged?.Invoke(); });
        MuteSystemCommand = new RelayCommand(() => { SystemMuted = !SystemMuted; MuteChanged?.Invoke(); });

        RefreshTargets();
        _streamKey = SafeLoad(_platform) ?? "";
        _session.StatusChanged += s => _ui.Post(() => Apply(s));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised after a mute button was pressed (the app applies <see cref="MicMuted"/>/<see cref="SystemMuted"/> to the live mixer).</summary>
    public event Action? MuteChanged;

    public ObservableCollection<CaptureTarget> Targets { get; } = new();
    public ICommand GoLiveCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand MuteMicCommand { get; }
    public ICommand MuteSystemCommand { get; }

    public CaptureTarget? SelectedTarget
    {
        get => _selected;
        set { if (Set(ref _selected, value)) Recompute(); }
    }

    public Platform Platform
    {
        get => _platform;
        set
        {
            if (!Set(ref _platform, value)) return;
            StreamKey = SafeLoad(value) ?? "";
            Notify(nameof(HelpUrl)); Notify(nameof(HelpText)); Notify(nameof(IsYouTube)); Notify(nameof(IsFacebook));
        }
    }

    public bool IsYouTube { get => Platform == Platform.YouTube; set { if (value) Platform = Platform.YouTube; } }
    public bool IsFacebook { get => Platform == Platform.Facebook; set { if (value) Platform = Platform.Facebook; } }

    public string StreamKey
    {
        get => _streamKey;
        set { if (Set(ref _streamKey, value ?? "")) { Notify(nameof(KeyHint)); Notify(nameof(HasKeyHint)); Recompute(); } }
    }

    /// <summary>Why the pasted text cannot be used as a key (null when the box is empty or fine).</summary>
    public string? KeyHint => KeyValidator.Explain(StreamKey);
    public bool HasKeyHint => KeyHint is not null;

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string QualityText { get => _qualityText; private set => Set(ref _qualityText, value); }

    /// <summary>Current state of the stream (used by the window to colour the status dot).</summary>
    public SessionState State => _state;
    public bool IsLive => _state == SessionState.Live;
    public bool IsBusy => _state is SessionState.Checking or SessionState.Live or SessionState.Reconnecting;
    public bool CanEdit => !IsBusy;
    public bool CanGoLive => !IsBusy && SelectedTarget is not null && KeyValidator.Normalize(StreamKey) is not null;

    public bool MicMuted { get => _micMuted; private set => Set(ref _micMuted, value); }
    public bool SystemMuted { get => _systemMuted; private set => Set(ref _systemMuted, value); }

    public string HelpUrl => Platform == Platform.Facebook
        ? "https://www.facebook.com/live/producer"
        : "https://www.youtube.com/live_dashboard";

    public string HelpText => Platform == Platform.Facebook
        ? "In Facebook Live Producer, choose Streaming software and copy the Stream key."
        : "In YouTube Studio, press Go live, then Stream, and copy the Stream key.";

    public void RefreshTargets()
    {
        IReadOnlyList<CaptureTarget> list;
        try { list = _listTargets(); } catch { list = Array.Empty<CaptureTarget>(); }
        var ordered = list.Where(t => t.Monitor != 0).Concat(list.Where(t => t.Monitor == 0)).ToList();
        var keep = SelectedTarget;
        Targets.Clear();
        foreach (var t in ordered) Targets.Add(t);
        SelectedTarget = keep is not null && ordered.Contains(keep) ? keep : null;
    }

    private void GoLive()
    {
        var key = KeyValidator.Normalize(StreamKey);
        var target = SelectedTarget;
        if (key is null || target is null || IsBusy) return;

        _runPlatform = Platform;
        _runWentLive = false;
        _runCts?.Dispose();
        var cts = _runCts = new CancellationTokenSource();
        _previousKey = SafeLoad(Platform);
        try { _keys.Save(Platform, key); }
        catch { Status = "EasyLive could not save your stream key on this computer."; return; }

        // Show progress at once; the orchestrator confirms it right away.
        Apply(new SessionStatus(SessionState.Checking, SessionOrchestrator.CheckingMessage, null), local: true);
        _ = RunGoLive(Platform, target, cts.Token);
    }

    private async Task RunGoLive(Platform platform, CaptureTarget target, CancellationToken ct)
    {
        try { await _session.GoLiveAsync(platform, target, ct).ConfigureAwait(false); }
        catch (Exception)
        {
            _ui.Post(() => Apply(new SessionStatus(SessionState.Failed,
                "Something went wrong and the stream could not start. Please try again.", null)));
        }
    }

    private async Task StopSafe()
    {
        // Also cancels a Go Live that is still waiting (e.g. for the previous stream to finish closing).
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { await _session.StopAsync().ConfigureAwait(false); } catch { /* shown through status */ }
    }

    private void Apply(SessionStatus s, bool local = false)
    {
        // A late status from a finished run must not unlock the window while a new run is starting.
        if (local && IsBusy) return;
        var runPlatform = _runPlatform;
        _state = s.State;
        if (s.State == SessionState.Live) { _runWentLive = true; }
        else if (!IsBusy && _runPlatform is { } p)
        {
            if (!_runWentLive) RestoreKey(p);
            _runPlatform = null;
        }

        Status = s.State == SessionState.Failed && s.Message == ErrorTexts.KeyRejected && (runPlatform ?? Platform) == Platform.Facebook
            ? FacebookKeyRejected
            : s.Message;
        QualityText = s.QualityText ?? "";
        Recompute();
    }

    /// <summary>Call when the app closes: synchronously undoes the key save if the stream never went live.</summary>
    public void Shutdown()
    {
        try { _runCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_runPlatform is { } p)
        {
            if (!_runWentLive) RestoreKey(p);
            _runPlatform = null;
        }
    }

    private void RestoreKey(Platform p)
    {
        try { if (_previousKey is null) _keys.Clear(p); else _keys.Save(p, _previousKey); } catch { }
    }

    private string? SafeLoad(Platform p) { try { return _keys.Load(p); } catch { return null; } }

    private void Recompute()
    {
        Notify(nameof(State)); Notify(nameof(CanGoLive)); Notify(nameof(IsBusy)); Notify(nameof(IsLive)); Notify(nameof(CanEdit));
        _goLive.RaiseCanExecuteChanged();
        _stop.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}




