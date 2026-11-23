using EasyLive.Core;
using EasyLive.Core.Capture;

namespace EasyLive.Core.Tests;

public class MainViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EasyLiveVmTests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeController _ctl = new();
    private readonly KeyStore _keys;
    private static readonly CaptureTarget Monitor = new("Monitor 1 (1920x1080)", 0, 5);
    private static readonly CaptureTarget Window = new("Notepad", 7, 0);

    public MainViewModelTests() => _keys = new KeyStore(Path.Combine(_dir, "keys.json"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class FakeController : ISessionController
    {
        public event Action<SessionStatus>? StatusChanged;
        public event Action<StreamDetails>? DetailsChanged;
        public void RaiseDetails(StreamDetails d) => DetailsChanged?.Invoke(d);
        public List<(Platform P, CaptureTarget T)> Calls = new();
        public int StopCalls;
        public bool BlockUntilCancelled;
        public CancellationToken LastToken;
        public Task GoLiveAsync(Platform p, CaptureTarget t, CancellationToken ct)
        {
            Calls.Add((p, t));
            LastToken = ct;
            if (!BlockUntilCancelled) return Task.CompletedTask;
            var tcs = new TaskCompletionSource();
            ct.Register(() => tcs.TrySetResult());
            return tcs.Task;
        }
        public Task StopAsync() { StopCalls++; return Task.CompletedTask; }
        public void Raise(SessionState s, string m, string? q = null) => StatusChanged?.Invoke(new SessionStatus(s, m, q));
    }

    private sealed class InlineDispatcher : IUiDispatcher { public void Post(Action a) => a(); }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        public readonly Queue<Action> Pending = new();
        public void Post(Action a) => Pending.Enqueue(a);
    }

    private MainViewModel Make(IUiDispatcher? d = null) =>
        new(_ctl, _keys, d ?? new InlineDispatcher(), () => new[] { Window, Monitor });

    [Fact]
    public void Targets_list_monitors_before_windows_and_nothing_is_preselected()
    {
        var vm = Make();
        Assert.Equal(new[] { Monitor, Window }, vm.Targets.ToArray());
        Assert.Null(vm.SelectedTarget);
    }

    [Fact]
    public void CanGoLive_needs_target_and_valid_key()
    {
        var vm = Make();
        Assert.False(vm.CanGoLive);
        vm.SelectedTarget = Monitor;
        Assert.False(vm.CanGoLive);
        vm.StreamKey = "ab";
        Assert.False(vm.CanGoLive);
        vm.StreamKey = "  abcd-1234-efgh  ";
        Assert.True(vm.CanGoLive);
        vm.SelectedTarget = null;
        Assert.False(vm.CanGoLive);
    }

    [Fact]
    public void CanGoLive_raises_property_changed_and_command_can_execute_changed()
    {
        var vm = Make();
        var props = new List<string?>();
        int canExec = 0;
        vm.PropertyChanged += (_, e) => props.Add(e.PropertyName);
        vm.GoLiveCommand.CanExecuteChanged += (_, _) => canExec++;
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        Assert.Contains(nameof(MainViewModel.CanGoLive), props);
        Assert.True(canExec > 0);
        Assert.True(vm.GoLiveCommand.CanExecute(null));
    }

    [Fact]
    public async Task GoLive_calls_orchestrator_with_platform_and_target()
    {
        var vm = Make();
        vm.Platform = Platform.Facebook;
        vm.SelectedTarget = Window;
        vm.StreamKey = " \"fb-key-1234\" ";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(new[] { (Platform.Facebook, Window) }, _ctl.Calls.ToArray());
    }

    [Fact]
    public async Task GoLive_stores_the_normalized_key_for_the_orchestrator_before_starting()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "  abcd-1234  ";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal("abcd-1234", _keys.Load(Platform.YouTube));
    }

    [Fact]
    public void GoLive_does_nothing_when_it_cannot_go_live()
    {
        var vm = Make();
        vm.GoLiveCommand.Execute(null);
        Assert.Empty(_ctl.Calls);
    }

    [Fact]
    public void Status_follows_StatusChanged()
    {
        var vm = Make();
        _ctl.Raise(SessionState.Checking, "Checking your computer and internet…");
        Assert.Equal("Checking your computer and internet…", vm.Status);
        _ctl.Raise(SessionState.Live, "You're live", "720p · 30 fps");
        Assert.Equal("You're live", vm.Status);
        Assert.Equal("720p · 30 fps", vm.QualityText);
        Assert.True(vm.IsLive);
        Assert.True(vm.IsBusy);
        _ctl.Raise(SessionState.Stopped, "Stream stopped");
        Assert.False(vm.IsLive);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Status_changes_are_marshalled_through_the_dispatcher()
    {
        var d = new QueueDispatcher();
        var vm = Make(d);
        var before = vm.Status;
        await Task.Run(() => _ctl.Raise(SessionState.Live, "You're live", "720p · 30 fps"));
        Assert.Equal(before, vm.Status);
        Assert.Single(d.Pending);
        d.Pending.Dequeue()();
        Assert.Equal("You're live", vm.Status);
    }

    [Fact]
    public async Task Facebook_key_rejection_tells_the_user_to_paste_a_new_key()
    {
        var vm = Make();
        vm.Platform = Platform.Facebook;
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "fb-key-1234";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Failed, EasyLive.Core.Streaming.ErrorTexts.KeyRejected);
        Assert.Contains("paste the new key from Facebook Live Producer", vm.Status);
        Assert.Equal(MainViewModel.FacebookKeyRejected, vm.Status);
    }

    [Fact]
    public async Task YouTube_key_rejection_keeps_the_generic_text()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "yt-key-1234";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Failed, EasyLive.Core.Streaming.ErrorTexts.KeyRejected);
        Assert.Equal(EasyLive.Core.Streaming.ErrorTexts.KeyRejected, vm.Status);
        Assert.DoesNotContain("Facebook", vm.Status);
    }

    [Fact]
    public async Task Stop_cancels_a_go_live_that_is_still_waiting()
    {
        _ctl.BlockUntilCancelled = true;
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(_ctl.LastToken.IsCancellationRequested);
        vm.StopCommand.Execute(null);
        Assert.True(_ctl.LastToken.IsCancellationRequested);
        Assert.Equal(1, _ctl.StopCalls);
    }

    [Fact]
    public async Task Shutdown_restores_the_unverified_key_and_cancels_the_run()
    {
        _keys.Save(Platform.YouTube, "old-key-1234");
        _ctl.BlockUntilCancelled = true;
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "new-key-5678";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal("new-key-5678", _keys.Load(Platform.YouTube));
        vm.Shutdown();
        Assert.Equal("old-key-1234", _keys.Load(Platform.YouTube));
        Assert.True(_ctl.LastToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Shutdown_keeps_the_key_of_a_stream_that_went_live()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Live, "You're live", "q");
        vm.Shutdown();
        Assert.Equal("abcd-1234", _keys.Load(Platform.YouTube));
    }
    [Fact]
    public async Task Key_is_kept_only_when_the_stream_went_live()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Checking, "x");
        _ctl.Raise(SessionState.Failed, "nope");
        Assert.Null(_keys.Load(Platform.YouTube));
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Live, "You're live", "720p · 30 fps");
        _ctl.Raise(SessionState.Failed, "dropped later");
        Assert.Equal("abcd-1234", _keys.Load(Platform.YouTube));
    }

    [Fact]
    public async Task A_failed_attempt_restores_the_previously_saved_key()
    {
        _keys.Save(Platform.YouTube, "old-key-1234");
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "new-key-5678";
        vm.GoLiveCommand.Execute(null);
        await Task.Delay(50);
        _ctl.Raise(SessionState.Failed, "nope");
        Assert.Equal("old-key-1234", _keys.Load(Platform.YouTube));
    }
    [Fact]
    public void Saved_key_is_filled_in_for_the_platform_and_nothing_on_first_run()
    {
        var vm = Make();
        Assert.Equal("", vm.StreamKey);
        _keys.Save(Platform.Facebook, "fb-saved-key");
        vm.Platform = Platform.Facebook;
        Assert.Equal("fb-saved-key", vm.StreamKey);
        vm.Platform = Platform.YouTube;
        Assert.Equal("", vm.StreamKey);
    }

    [Fact]
    public void Stop_command_stops_and_is_only_available_while_busy()
    {
        var vm = Make();
        Assert.False(vm.StopCommand.CanExecute(null));
        _ctl.Raise(SessionState.Live, "You're live", "q");
        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.Equal(1, _ctl.StopCalls);
    }

    [Fact]
    public void Inputs_are_locked_and_go_live_disabled_while_busy()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        Assert.True(vm.CanGoLive);
        _ctl.Raise(SessionState.Live, "You're live", "q");
        Assert.False(vm.CanGoLive);
        Assert.False(vm.CanEdit);
    }

    [Fact]
    public void Mute_commands_toggle_and_raise_MuteChanged()
    {
        var vm = Make();
        int changes = 0;
        vm.MuteChanged += () => changes++;
        vm.MuteMicCommand.Execute(null);
        Assert.True(vm.MicMuted);
        Assert.False(vm.SystemMuted);
        vm.MuteSystemCommand.Execute(null);
        Assert.True(vm.SystemMuted);
        vm.MuteMicCommand.Execute(null);
        Assert.False(vm.MicMuted);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void KeyHint_follows_the_key_box()
    {
        var vm = Make();
        var props = new List<string?>();
        vm.PropertyChanged += (_, e) => props.Add(e.PropertyName);
        Assert.Null(vm.KeyHint);
        vm.StreamKey = "rtmps://a.rtmps.youtube.com:443/live2";
        Assert.Equal(KeyValidator.ServerAddressHint, vm.KeyHint);
        Assert.True(vm.HasKeyHint);
        Assert.Contains(nameof(MainViewModel.KeyHint), props);
        vm.StreamKey = "abcd-1234-efgh";
        Assert.Null(vm.KeyHint);
        Assert.False(vm.HasKeyHint);
        vm.StreamKey = "";
        Assert.Null(vm.KeyHint);
    }
    private static StreamDetails Details(EncoderKind e = EncoderKind.QuickSync, int dropped = 3) =>
        new(Platform.YouTube, "Monitor 1 (1920x1080)", e, 1280, 720, 30, 1700, 600, 1500, 12.34, dropped, 80, TimeSpan.FromSeconds(3725));

    private static string Row(MainViewModel vm, string label) =>
        vm.DetailRows.Single(r => r.Label == label).Value;

    [Fact]
    public void Idle_details_show_platform_source_and_the_automatic_quality_hint()
    {
        var vm = Make();
        vm.SelectedTarget = Window;
        vm.Platform = Platform.Facebook;
        Assert.Equal("Facebook", Row(vm, "Platform"));
        Assert.Equal("Notepad", Row(vm, "Source"));
        Assert.Equal(2, vm.DetailRows.Count);
        Assert.Equal("Quality is chosen automatically when you press Go Live.", vm.DetailsHint);
    }

    [Fact]
    public void Live_details_are_worded_in_plain_language()
    {
        var vm = Make();
        _ctl.RaiseDetails(Details());
        Assert.Equal("YouTube", Row(vm, "Platform"));
        Assert.Equal("Monitor 1 (1920x1080)", Row(vm, "Source"));
        Assert.Equal("1280 x 720 at 30 frames per second", Row(vm, "Picture"));
        Assert.Equal("1,700 kbps now (allowed 600 to 1,500)", Row(vm, "Video bitrate"));
        Assert.Equal("Intel Quick Sync (hardware)", Row(vm, "Encoder"));
        Assert.Equal("AAC 128 kbps, 48 kHz stereo. Microphone on, game sound on.", Row(vm, "Audio"));
        Assert.Equal("12.3 Mbps measured", Row(vm, "Upload speed"));
        Assert.Equal("Every 2 seconds", Row(vm, "Keyframes"));
        Assert.Equal("3 dropped", Row(vm, "Dropped frames"));
        Assert.Equal("80 ms waiting to send", Row(vm, "Send delay"));
        Assert.Equal("1:02:05", Row(vm, "Live for"));
        Assert.Equal("", vm.DetailsHint);
    }

    [Theory]
    [InlineData(EncoderKind.Amf, "AMD AMF (hardware)")]
    [InlineData(EncoderKind.Nvenc, "NVIDIA NVENC (hardware)")]
    [InlineData(EncoderKind.OpenH264, "Software (OpenH264, capped at 720p30)")]
    public void Encoder_names(EncoderKind kind, string text)
    {
        var vm = Make();
        _ctl.RaiseDetails(Details(kind));
        Assert.Equal(text, Row(vm, "Encoder"));
    }

    [Fact]
    public void Audio_row_follows_the_mute_buttons_and_details_marshal_through_the_dispatcher()
    {
        var d = new QueueDispatcher();
        var vm = Make(d);
        _ctl.RaiseDetails(Details());
        Assert.Equal(2, vm.DetailRows.Count);   // not applied yet
        d.Pending.Dequeue()();
        vm.MuteMicCommand.Execute(null);
        Assert.Contains("Microphone muted", Row(vm, "Audio"));
        vm.MuteSystemCommand.Execute(null);
        Assert.Contains("game sound muted", Row(vm, "Audio"));
    }

    [Fact]
    public void Details_stay_after_the_stream_ends_and_clear_when_a_new_run_starts()
    {
        var vm = Make();
        vm.SelectedTarget = Monitor;
        vm.StreamKey = "abcd-1234";
        _ctl.RaiseDetails(Details());
        _ctl.Raise(SessionState.Stopped, "Stream stopped");
        Assert.Equal("3 dropped", Row(vm, "Dropped frames"));
        vm.GoLiveCommand.Execute(null);
        Assert.Equal(2, vm.DetailRows.Count);
    }
    [Fact]
    public void Help_text_and_url_exist_for_each_platform()
    {
        var vm = Make();
        Assert.StartsWith("https://", vm.HelpUrl);
        var yt = vm.HelpUrl;
        vm.Platform = Platform.Facebook;
        Assert.StartsWith("https://", vm.HelpUrl);
        Assert.NotEqual(yt, vm.HelpUrl);
    }

    [Fact]
    public void Refresh_reloads_targets_and_keeps_selection_when_still_present()
    {
        var vm = Make();
        vm.SelectedTarget = Window;
        vm.RefreshTargets();
        Assert.Equal(Window, vm.SelectedTarget);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void Second_acquire_of_the_same_name_fails_until_released()
    {
        var name = "EasyLiveTest-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryAcquire(name));
        try { Assert.False(SingleInstance.TryAcquire(name)); }
        finally { SingleInstance.Release(name); }
    }

    [Fact]
    public void Acquire_works_again_after_release()
    {
        var name = "EasyLiveTest-" + Guid.NewGuid().ToString("N");
        Assert.True(SingleInstance.TryAcquire(name));
        SingleInstance.Release(name);
        Assert.True(SingleInstance.TryAcquire(name));
        SingleInstance.Release(name);
    }
}








