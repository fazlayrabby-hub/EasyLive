using EasyLive.Core.Audio;
using EasyLive.Core.Capture;
using EasyLive.Core.Streaming;
using Microsoft.Extensions.Time.Testing;

namespace EasyLive.Core.Tests;

public sealed class SessionOrchestratorTests : IDisposable
{
    // ---------- fakes ----------

    private sealed class FakeAdapters : IAdapterSource
    {
        public IReadOnlyList<string> Names { get; set; } = new[] { "Intel(R) Iris(R) Xe Graphics" };
        public IReadOnlyList<string> AdapterNames() => Names;
        public int CpuCores() => 8;
    }

    private sealed class FakeProbe : IProbeTransport
    {
        public double UploadMbps { get; set; } = 3;     // budget 1800 kbps -> 720p30 on QuickSync
        public bool Offline { get; set; }
        public TaskCompletionSource? UploadGate { get; set; }

        public Task<double> ConnectMsAsync(string host, int port, CancellationToken ct) =>
            Offline ? Task.FromException<double>(new IOException("offline")) : Task.FromResult(20.0);

        public async Task<(long bytes, TimeSpan time)> UploadAsync(TimeSpan duration, CancellationToken ct)
        {
            if (Offline) throw new IOException("offline");
            if (UploadGate is { } g) await g.Task.WaitAsync(ct);
            return ((long)(UploadMbps * 1e6 / 8 * 4), TimeSpan.FromSeconds(4));
        }
    }

    private sealed class FakeAudioInput : IAudioInput
    {
        public event Action<AudioChunk>? Data;
        public event Action? Lost;
        public bool Started { get; private set; }
        public void Start() => Started = true;
        public void Push() => Data?.Invoke(new AudioChunk(new float[960], 0));
        public void Lose() => Lost?.Invoke();
    }

    private sealed class FakeSession : IStreamSession
    {
        private readonly List<Action<string>> _everSubscribed = new();
        private Action<string>? _failed;
        public event Action<string>? Failed
        {
            add { _failed += value; if (value is not null) _everSubscribed.Add(value); }
            remove { _failed -= value; }
        }
        /// <summary>Like a real session's Failed already queued on the thread pool when the handler was removed.</summary>
        public void FailStale(string message) { foreach (var h in _everSubscribed.ToList()) h(message); }
        private Action<string>? FailedHandlers => _failed;
        public Exception? StartThrows { get; set; }
        public TaskCompletionSource? StartGate { get; set; }
        public string? Url { get; private set; }
        public StreamPlan? Plan { get; private set; }
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public List<int> Bitrates { get; } = new();
        public List<QualityRung> Reinits { get; } = new();
        public int VideoPushes;
        public int AudioPushes;
        public StreamHealth Health { get; set; } = new(0, 0, 0, 1800);

        public async Task StartAsync(string rtmpUrlWithKey, StreamPlan plan, CancellationToken ct)
        {
            Url = rtmpUrlWithKey;
            Plan = plan;
            if (StartGate is { } g) await g.Task.WaitAsync(ct);
            if (StartThrows is { } ex)
            {
                if (ex is StreamSessionException) FailedHandlers?.Invoke(ex.Message);
                throw ex;
            }
            Started = true;
        }

        public void PushVideo(CaptureFrame f) => Interlocked.Increment(ref VideoPushes);
        public void PushAudio(AudioChunk c) => Interlocked.Increment(ref AudioPushes);
        public void SetBitrate(int kbps) { lock (Bitrates) Bitrates.Add(kbps); }
        public Task ReinitAsync(QualityRung lower) { lock (Reinits) Reinits.Add(lower); return Task.CompletedTask; }
        public void Fail(string message) => FailedHandlers?.Invoke(message);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingFrameSource : IFrameSource
    {
        public event Action<CaptureFrame>? FrameArrived;
        public event Action<string>? Closed;
        public CaptureTarget? Target { get; private set; }
        public bool Running { get; private set; }
        public int Starts, Stops;
        public bool HasSubscribers => FrameArrived is not null || Closed is not null;
        public void Start(CaptureTarget target) { Target = target; Running = true; Starts++; }
        public void Stop() { Running = false; Stops++; }
        public void Raise() => FrameArrived?.Invoke(new CaptureFrame(1, 1920, 1080, 0));
        public void RaiseClosed(string reason) => Closed?.Invoke(reason);
        public ValueTask DisposeAsync() { Stop(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeSampler : IFrameSampler
    {
        public byte[]? Next { get; set; } = Bright;
        public int Calls;
        public byte[]? Sample(CaptureFrame f) { Calls++; return Next; }
        public static byte[] Black => new byte[64 * 4];
        public static byte[] Bright => Enumerable.Repeat((byte)200, 64 * 4).ToArray();
    }

    // ---------- harness ----------

    private static readonly CaptureTarget Monitor = new("Display 1", 0, 1);
    private const string Key = "abcd-efgh-ijkl-mnop";

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"easylive-orch-{Guid.NewGuid():N}.json");
    private readonly FakeTimeProvider _clock = new();
    private readonly FakeAdapters _adapters = new();
    private readonly FakeProbe _probe = new();
    private readonly RecordingFrameSource _frames = new();
    private readonly FakeSampler _sampler = new();
    private readonly List<FakeSession> _sessions = new();
    private readonly List<FakeAudioInput> _systemInputs = new();
    private readonly Queue<Action<FakeSession>> _sessionSetups = new();
    private readonly List<SessionStatus> _statuses = new();
    private readonly KeyStore _keys;
    private readonly SessionOrchestrator _orch;

    public SessionOrchestratorTests()
    {
        _keys = new KeyStore(_keyPath);
        _orch = new SessionOrchestrator(_adapters, _probe, _frames,
            () =>
            {
                var sys = new FakeAudioInput();
                lock (_systemInputs) _systemInputs.Add(sys);
                return new AudioMixer(sys, new FakeAudioInput(), _clock);
            },
            () =>
            {
                var s = new FakeSession();
                lock (_sessions)
                {
                    if (_sessionSetups.Count > 0) _sessionSetups.Dequeue()(s);
                    _sessions.Add(s);
                }
                return s;
            },
            _keys, _sampler, _clock);
        _orch.StatusChanged += s => { lock (_statuses) _statuses.Add(s); };
    }

    public void Dispose()
    {
        try { _orch.StopAsync().Wait(5000); } catch { }
        try { File.Delete(_keyPath); } catch { }
    }

    private FakeSession LastSession { get { lock (_sessions) return _sessions[^1]; } }
    private int SessionCount { get { lock (_sessions) return _sessions.Count; } }
    private List<SessionStatus> Statuses { get { lock (_statuses) return _statuses.ToList(); } }

    private static void WaitFor(Func<bool> cond, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cond())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }

    private void Advance(int seconds)
    {
        for (int i = 0; i < seconds; i++) _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private async Task GoLiveAsync()
    {
        _keys.Save(Platform.YouTube, Key);
        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);
        Assert.Equal(SessionState.Live, _orch.Status.State);
    }

    // ---------- Go Live checks ----------

    [Fact]
    public async Task No_saved_key_fails_with_paste_message()
    {
        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);

        Assert.Equal(SessionState.Failed, _orch.Status.State);
        Assert.Equal("Paste your stream key first", _orch.Status.Message);
        Assert.Equal(SessionState.Checking, Statuses[0].State);
        Assert.Equal(0, SessionCount);
    }

    [Fact]
    public async Task Offline_probe_fails_with_no_internet()
    {
        _keys.Save(Platform.YouTube, Key);
        _probe.Offline = true;

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);

        Assert.Equal(SessionState.Failed, _orch.Status.State);
        Assert.Equal("No internet connection", _orch.Status.Message);
        Assert.Equal(0, SessionCount);
    }

    [Fact]
    public async Task Too_slow_upload_fails()
    {
        _keys.Save(Platform.YouTube, Key);
        _probe.UploadMbps = 1;   // budget 600 kbps < 700

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);

        Assert.Equal(SessionState.Failed, _orch.Status.State);
        Assert.Equal("Your upload speed is too slow to stream", _orch.Status.Message);
        Assert.Equal(0, SessionCount);
    }

    [Fact]
    public async Task Happy_path_goes_live_with_quality_text_and_wires_media()
    {
        await GoLiveAsync();

        Assert.Equal("720p \u00B7 30 fps", _orch.Status.QualityText);
        Assert.Equal(SessionOrchestrator.LiveMessage, _orch.Status.Message.Split('.')[0]);
        var s = LastSession;
        Assert.True(s.Started);
        Assert.Equal("rtmps://a.rtmps.youtube.com:443/live2/" + Key, s.Url);
        Assert.Equal("720p30", s.Plan!.Rung.Name);
        Assert.InRange(s.Plan.StartKbps, 1795, 1800);    // 60 % of the measured 3 Mbps (float rounding)
        Assert.True(_frames.Running);
        Assert.Equal(Monitor, _frames.Target);
        Assert.True(_systemInputs[^1].Started);

        _frames.Raise();
        _systemInputs[^1].Push();
        Assert.Equal(1, s.VideoPushes);
        Assert.True(s.AudioPushes >= 1);
        Assert.DoesNotContain(Statuses, st => st.Message.Contains(Key));
    }

    [Fact]
    public async Task Facebook_url_is_ingest_plus_key()
    {
        _keys.Save(Platform.Facebook, Key);
        await _orch.GoLiveAsync(Platform.Facebook, Monitor, CancellationToken.None);
        Assert.Equal("rtmps://live-api-s.facebook.com:443/rtmp/" + Key, LastSession.Url);
    }

    [Fact]
    public async Task Mic_missing_notice_while_starting_shows_in_live_message()
    {
        _keys.Save(Platform.YouTube, Key);
        var orch = new SessionOrchestrator(_adapters, _probe, _frames,
            () => new AudioMixer(new FakeAudioInput(), null, _clock),
            () => { var s = new FakeSession(); lock (_sessions) _sessions.Add(s); return s; },
            _keys, _sampler, _clock);

        await orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);

        Assert.Equal(SessionState.Live, orch.Status.State);
        Assert.Contains(AudioMixer.NoMicMessage, orch.Status.Message);
        await orch.StopAsync();
    }

    [Fact]
    public async Task Mixer_notice_while_live_updates_message_state_unchanged()
    {
        await GoLiveAsync();
        // the mic of the last mixer is the second FakeAudioInput; easiest: raise system Lost
        _systemInputs[^1].Lose();
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.Contains(AudioMixer.SystemLostMessage, _orch.Status.Message);
    }

    [Fact]
    public async Task Start_failure_during_go_live_fails_without_reconnect()
    {
        _keys.Save(Platform.YouTube, Key);
        _sessionSetups.Enqueue(s => s.StartThrows = new StreamSessionException(ErrorTexts.CouldNotConnect));

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);

        Assert.Equal(SessionState.Failed, _orch.Status.State);
        Assert.Equal(ErrorTexts.CouldNotConnect, _orch.Status.Message);
        Advance(30);
        Assert.Equal(1, SessionCount);
        Assert.True(LastSession.Disposed);
        Assert.False(_frames.Running);
    }

    [Fact]
    public async Task Cancelling_go_live_tears_down_and_stops()
    {
        _keys.Save(Platform.YouTube, Key);
        var gate = new TaskCompletionSource();
        _sessionSetups.Enqueue(s => s.StartGate = gate);
        using var cts = new CancellationTokenSource();

        var task = _orch.GoLiveAsync(Platform.YouTube, Monitor, cts.Token);
        WaitFor(() => SessionCount == 1, "session created");
        cts.Cancel();
        await task;

        Assert.Equal(SessionState.Stopped, _orch.Status.State);
        Assert.True(LastSession.Disposed);
        Assert.False(_frames.Running);
    }

    // ---------- adaptation ----------

    [Fact]
    public async Task Congested_health_for_30_s_drives_bitrate_down_then_reinit_to_lower_rung()
    {
        await GoLiveAsync();
        var s = LastSession;
        s.Health = new StreamHealth(2000, 0, 0, 1800);   // send queue congested

        Advance(30);

        Assert.Contains(1500, s.Bitrates);                // 1800 * 0.75 = 1350 -> floor 1500
        Assert.Single(s.Reinits);
        Assert.Equal("480p30", s.Reinits[0].Name);
        Assert.Equal("480p \u00B7 30 fps", _orch.Status.QualityText);
        Assert.Contains(750, s.Bitrates);                 // new controller at 480p30 (1000) keeps stepping down
        Assert.Equal(SessionState.Live, _orch.Status.State);
    }

    [Fact]
    public async Task Dropped_frames_count_as_delta_not_total()
    {
        await GoLiveAsync();
        var s = LastSession;
        s.Health = new StreamHealth(0, 5, 0, 1800);      // 5 drops before the first tick
        Advance(1);
        Assert.Single(s.Bitrates);                       // delta 5 -> congested once
        Advance(5);                                      // total unchanged -> healthy
        Assert.Single(s.Bitrates);
    }

    [Fact]
    public async Task Reinit_at_lowest_rung_is_ignored()
    {
        _probe.UploadMbps = 1.5;   // budget 900 -> 480p30
        await GoLiveAsync();
        Assert.Equal("480p \u00B7 30 fps", _orch.Status.QualityText);
        var s = LastSession;
        s.Health = new StreamHealth(2000, 0, 0, 900);

        Advance(40);

        Assert.Empty(s.Reinits);
        Assert.Equal(SessionState.Live, _orch.Status.State);
    }

    // ---------- reconnect ----------

    [Fact]
    public async Task Failed_goes_reconnecting_then_live_when_retry_succeeds_and_restarts_capture()
    {
        await GoLiveAsync();
        var first = LastSession;
        int startsBefore = _frames.Starts;

        first.Fail(ErrorTexts.ConnectionLost);

        Assert.Equal(SessionState.Reconnecting, _orch.Status.State);
        Assert.Equal(SessionOrchestrator.ReconnectingMessage, _orch.Status.Message);
        WaitFor(() => first.Disposed, "old session disposed");
        _frames.Raise();
        Assert.Equal(0, first.VideoPushes);              // no pushes to a dead session

        Advance(4);
        Assert.Equal(1, SessionCount);
        Advance(1);
        WaitFor(() => _orch.Status.State == SessionState.Live, "live again");

        var second = LastSession;
        Assert.Equal(2, SessionCount);
        Assert.Equal(first.Url, second.Url);
        Assert.Equal("720p30", second.Plan!.Rung.Name);
        Assert.Equal(startsBefore + 1, _frames.Starts);
        Assert.True(_frames.Running);
        _frames.Raise();
        Assert.Equal(1, second.VideoPushes);
    }

    [Fact]
    public async Task Still_failing_after_120_s_fails_with_plain_message()
    {
        await GoLiveAsync();
        for (int i = 0; i < 40; i++)
            _sessionSetups.Enqueue(s => s.StartThrows = new StreamSessionException(ErrorTexts.CouldNotConnect));

        LastSession.Fail(ErrorTexts.ConnectionLost);
        Advance(115);
        Assert.Equal(SessionState.Reconnecting, _orch.Status.State);
        int attempts = SessionCount - 1;
        Assert.InRange(attempts, 22, 23);

        Advance(6);
        WaitFor(() => _orch.Status.State == SessionState.Failed, "failed after 120 s");
        Assert.Equal(SessionOrchestrator.ConnectionNotRestoredMessage, _orch.Status.Message);
        Assert.False(_frames.Running);
        int count = SessionCount;
        Advance(30);
        Assert.Equal(count, SessionCount);               // no more retries
    }

    [Fact]
    public async Task Key_rejected_fails_without_retry()
    {
        await GoLiveAsync();
        LastSession.Fail(ErrorTexts.KeyRejected);

        WaitFor(() => _orch.Status.State == SessionState.Failed, "failed");
        Assert.Equal(ErrorTexts.KeyRejected, _orch.Status.Message);
        Advance(30);
        Assert.Equal(1, SessionCount);
        WaitFor(() => LastSession.Disposed, "disposed");
    }

    [Fact]
    public async Task Key_rejected_on_retry_fails_at_once()
    {
        await GoLiveAsync();
        _sessionSetups.Enqueue(s => s.StartThrows = new StreamSessionException(ErrorTexts.KeyRejected));
        LastSession.Fail(ErrorTexts.ConnectionLost);
        Advance(5);
        WaitFor(() => _orch.Status.State == SessionState.Failed, "failed");
        Assert.Equal(ErrorTexts.KeyRejected, _orch.Status.Message);
        Advance(30);
        Assert.Equal(2, SessionCount);
    }

    // ---------- capture ----------

    [Fact]
    public async Task Capture_closed_shows_message_and_stays_live_then_a_frame_clears_it()
    {
        await GoLiveAsync();
        _frames.RaiseClosed("The window you were sharing was closed.");

        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.Contains("window", _orch.Status.Message);

        Advance(5);
        _frames.Raise();
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.DoesNotContain("window", _orch.Status.Message);
        Advance(10);
        Assert.Equal(SessionState.Live, _orch.Status.State);   // hold cancelled
    }

    [Fact]
    public async Task No_frame_for_10_s_after_closed_fails()
    {
        await GoLiveAsync();
        _frames.RaiseClosed("The window you were sharing was closed.");

        Advance(9);
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Advance(1);
        WaitFor(() => _orch.Status.State == SessionState.Failed, "failed");
        Assert.Equal("Stopped \u2014 The window you were sharing was closed.", _orch.Status.Message);
        WaitFor(() => LastSession.Disposed, "disposed");
        Assert.False(_frames.Running);
    }

    [Fact]
    public async Task Blank_capture_warns_but_stays_live_and_clears()
    {
        await GoLiveAsync();
        _sampler.Next = FakeSampler.Black;

        for (int i = 0; i < 8; i++)                       // 0 .. 3.5 s
        {
            _frames.Raise();
            _clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        _frames.Raise();                                  // 4 s
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.Equal(SessionOrchestrator.BlankWarning, _orch.Status.Message);

        _sampler.Next = FakeSampler.Bright;
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        _frames.Raise();
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.NotEqual(SessionOrchestrator.BlankWarning, _orch.Status.Message);
    }

    [Fact]
    public async Task Sampler_is_called_at_most_every_500_ms()
    {
        await GoLiveAsync();
        for (int i = 0; i < 10; i++)
        {
            _frames.Raise();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.InRange(_sampler.Calls, 2, 3);
    }

    // ---------- stop ----------

    [Fact]
    public async Task Stop_tears_down_and_a_second_go_live_works()
    {
        await GoLiveAsync();
        var first = LastSession;

        await _orch.StopAsync();

        Assert.Equal(SessionState.Stopped, _orch.Status.State);
        Assert.True(first.Disposed);
        Assert.False(_frames.Running);
        Assert.False(_frames.HasSubscribers);
        await _orch.StopAsync();                           // idempotent
        Assert.Equal(SessionState.Stopped, _orch.Status.State);

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Assert.Equal(2, SessionCount);
        _frames.Raise();
        Assert.Equal(1, LastSession.VideoPushes);
        Assert.Equal(0, first.VideoPushes);
    }

    [Fact]
    public async Task Stale_failed_from_old_session_after_stop_does_not_change_state()
    {
        await GoLiveAsync();
        var first = LastSession;
        await _orch.StopAsync();

        first.FailStale(ErrorTexts.ConnectionLost);
        Assert.Equal(SessionState.Stopped, _orch.Status.State);

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);
        first.FailStale(ErrorTexts.KeyRejected);
        first.FailStale(ErrorTexts.ConnectionLost);
        Assert.Equal(SessionState.Live, _orch.Status.State);
        Advance(10);
        Assert.Equal(2, SessionCount);
        Assert.False(LastSession.Disposed);
    }

    [Fact]
    public async Task Stale_failed_from_replaced_session_after_reconnect_is_ignored()
    {
        await GoLiveAsync();
        var first = LastSession;
        first.Fail(ErrorTexts.ConnectionLost);
        Advance(5);
        WaitFor(() => _orch.Status.State == SessionState.Live, "live again");

        first.FailStale(ErrorTexts.ConnectionLost);
        first.FailStale(ErrorTexts.KeyRejected);

        Assert.Equal(SessionState.Live, _orch.Status.State);
        Advance(10);
        Assert.Equal(2, SessionCount);
    }

    [Fact]
    public async Task Status_changed_handler_may_call_stop_without_deadlock()
    {
        _keys.Save(Platform.YouTube, Key);
        Task? stop = null;
        _orch.StatusChanged += s => { if (s.State == SessionState.Live) stop = _orch.StopAsync(); };

        await _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);
        Assert.NotNull(stop);
        await stop!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionState.Stopped, _orch.Status.State);
        var order = Statuses.Select(s => s.State).ToList();
        Assert.Equal(new[] { SessionState.Checking, SessionState.Live, SessionState.Stopped }, order);
    }

    [Fact]
    public async Task Stop_while_checking_stops_cleanly()
    {
        _keys.Save(Platform.YouTube, Key);
        _probe.UploadGate = new TaskCompletionSource();

        var task = _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None);
        Assert.Equal(SessionState.Checking, _orch.Status.State);
        await _orch.StopAsync();
        await task;

        Assert.Equal(SessionState.Stopped, _orch.Status.State);
        Assert.Equal(0, SessionCount);
        Assert.False(_frames.Running);
    }

    [Fact]
    public async Task Stop_while_reconnecting_stops_retries()
    {
        await GoLiveAsync();
        LastSession.Fail(ErrorTexts.ConnectionLost);
        Assert.Equal(SessionState.Reconnecting, _orch.Status.State);

        await _orch.StopAsync();
        Assert.Equal(SessionState.Stopped, _orch.Status.State);
        Advance(30);
        Assert.Equal(1, SessionCount);
        Assert.Equal(SessionState.Stopped, _orch.Status.State);
    }

    [Fact]
    public async Task Go_live_while_live_throws()
    {
        await GoLiveAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _orch.GoLiveAsync(Platform.YouTube, Monitor, CancellationToken.None));
        Assert.Equal(SessionState.Live, _orch.Status.State);
    }
}
