using System.Diagnostics;
using EasyLive.Core.Audio;
using EasyLive.Core.Capture;
using EasyLive.Core.Streaming;

namespace EasyLive.Core;

public enum SessionState { Idle, Checking, Live, Reconnecting, Stopped, Failed }

/// <param name="Message">Plain-words status line for the user (never contains the stream key).</param>
/// <param name="QualityText">E.g. "720p · 30 fps" while Live/Reconnecting, otherwise null.</param>
public sealed record SessionStatus(SessionState State, string Message, string? QualityText);

/// <summary>
/// The Go Live state machine: checks (key, hardware, network, plan), starts the stream session, wires capture and
/// audio into it, adapts the bitrate once a second, reconnects after a lost connection (every 5 s, up to 2 min),
/// and reports capture problems (window closed/minimized, black picture) in plain words.
/// <para>
/// Threading: events arrive on capture, audio, timer and thread-pool threads. All state lives in a per-Go-Live
/// <see cref="Run"/> object; every callback is bound to its run and ignored once that run is no longer current
/// (stopped, failed or replaced), and session callbacks are also bound to their session. State changes happen under
/// <c>_gate</c>; <see cref="StatusChanged"/> is always raised after the lock is released, in order, from one thread
/// at a time (a handler may call <see cref="StopAsync"/>). No lock is held around pushing media or calling into
/// the session/mixer, except capture Start/Stop which are serialised under their own lock.
/// </para>
/// </summary>
public sealed class SessionOrchestrator
{
    public const string CheckingMessage = "Checking your computer and internet…";
    public const string NoKeyMessage = "Paste your stream key first";
    public const string OfflineMessage = "No internet connection";
    public const string TooSlowMessage = "Your upload speed is too slow to stream";
    public const string LiveMessage = "You're live";
    public const string ReconnectingMessage = "Reconnecting…";
    public const string StoppedMessage = "Stream stopped";
    public const string ConnectionNotRestoredMessage =
        "The connection could not be restored — the stream has stopped. Check your internet and press Go Live again.";
    public const string BlankWarning =
        "Your screen is showing only black — if you're sharing a game, run it in borderless or windowed mode.";
    public const string CaptureFailedMessage = "The screen or window you picked could not be captured.";
    public const string AudioFailedMessage = "Your computer's sound could not be captured.";
    public const string StoppedPrefix = "Stopped — ";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDeadline = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan ClosedHold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BlankThreshold = TimeSpan.FromSeconds(3);

    private readonly IAdapterSource _adapters;
    private readonly IProbeTransport _probe;
    private readonly IFrameSource _frames;
    private readonly Func<AudioMixer> _mixerFactory;
    private readonly Func<IStreamSession> _sessionFactory;
    private readonly KeyStore _keys;
    private readonly IFrameSampler _sampler;
    private readonly TimeProvider _clock;

    private readonly object _gate = new();
    private readonly object _captureGate = new();          // serialises capture Start/Stop and event wiring
    private readonly Queue<SessionStatus> _pending = new();
    private bool _raising;
    private SessionStatus _status = new(SessionState.Idle, "", null);
    private volatile Run? _run;
    private Task _lastTeardown = Task.CompletedTask;

    public SessionOrchestrator(IAdapterSource a, IProbeTransport p, IFrameSource f, Func<AudioMixer> mixer,
                               Func<IStreamSession> session, KeyStore k, IFrameSampler sampler, TimeProvider clock)
    {
        _adapters = a ?? throw new ArgumentNullException(nameof(a));
        _probe = p ?? throw new ArgumentNullException(nameof(p));
        _frames = f ?? throw new ArgumentNullException(nameof(f));
        _mixerFactory = mixer ?? throw new ArgumentNullException(nameof(mixer));
        _sessionFactory = session ?? throw new ArgumentNullException(nameof(session));
        _keys = k ?? throw new ArgumentNullException(nameof(k));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Server URL (without key) per platform. Overridable for local testing (e.g. a MediaMTX sink).</summary>
    public Func<Platform, string> IngestUrlFor { get; init; } = Platforms.IngestUrl;

    /// <summary>Raised after every status change, in order, never while an internal lock is held.</summary>
    public event Action<SessionStatus>? StatusChanged;

    public SessionStatus Status { get { lock (_gate) return _status; } }

    // ------------------------------------------------------------------ Go Live

    /// <summary>
    /// Runs the checks and starts streaming. Completes when the stream is Live or the attempt ended (Failed/Stopped);
    /// failures are reported through <see cref="Status"/>, not exceptions. Cancelling <paramref name="ct"/> during the
    /// attempt stops it cleanly (state Stopped); it has no effect once Live (use <see cref="StopAsync"/>).
    /// Throws <see cref="InvalidOperationException"/> if a stream is already starting or running.
    /// </summary>
    public async Task GoLiveAsync(Platform p, CaptureTarget t, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(t);
        Task previous;
        lock (_gate)
        {
            if (_run is not null) throw new InvalidOperationException("A stream is already starting or running.");
            previous = _lastTeardown;
        }
        await previous.ConfigureAwait(false);   // the previous run's devices/connection are released first

        var run = new Run(p, t) { StartTs = _clock.GetTimestamp() };
        lock (_gate)
        {
            if (_run is not null) throw new InvalidOperationException("A stream is already starting or running.");
            _run = run;
            SetStatus(new SessionStatus(SessionState.Checking, CheckingMessage, null));
        }
        Publish();

        string? failure;
        var endState = SessionState.Failed;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Cts.Token);
        try
        {
            failure = await GoLiveCoreAsync(run, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            failure = StoppedMessage;
            endState = SessionState.Stopped;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Go Live failed: {ex.GetType().Name}");
            failure = ErrorTexts.Unexpected;
        }
        finally
        {
            run.Startup.TrySetResult();   // teardown may now release what this attempt created
        }
        if (failure is not null) await EndRunAsync(run, endState, failure).ConfigureAwait(false);
    }

    /// <summary>Returns null when Live (or stopped meanwhile), otherwise the plain failure message.</summary>
    private async Task<string?> GoLiveCoreAsync(Run run, CancellationToken ct)
    {
        var stored = _keys.Load(run.Platform);
        var key = stored is null ? null : KeyValidator.Normalize(stored);
        if (key is null) return NoKeyMessage;

        var hw = HardwareDetector.Detect(_adapters);
        NetworkProbeResult net;
        try
        {
            net = await new NetworkProbe(_probe, _clock).RunAsync(Platforms.IngestHost(run.Platform), ct).ConfigureAwait(false);
        }
        catch (OfflineException)
        {
            return OfflineMessage;
        }
        ct.ThrowIfCancellationRequested();

        var plan = QualityPlanner.Choose(hw, net);
        if (plan.TooSlow) return TooSlowMessage;

        // Teardown waits for run.Startup, so registering resources on the run is safe even if Stop came meanwhile.
        var mixer = _mixerFactory();
        run.OnNotice = n => OnMixerNotice(run, n);
        mixer.Notice += run.OnNotice;                        // before Start(): "no mic" is raised from Start
        lock (_gate) run.Mixer = mixer;

        var slot = CreateSlot(run);
        var url = BuildUrl(run.Platform, key);
        lock (_gate)
        {
            run.Pending = slot;
            run.Url = url;
            run.Plan = plan;
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            await slot.Session.StartAsync(url, plan, ct).ConfigureAwait(false);
        }
        catch (StreamSessionException ex)
        {
            return ex.Message;                               // never live yet: no reconnect loop
        }

        lock (_gate)
        {
            run.Pending = null;
            run.Session = slot;
        }
        run.OnMixed = c => OnMixed(run, c);
        mixer.Mixed += run.OnMixed;
        run.OnFrame = f => OnFrame(run, f);
        run.OnClosed = m => OnCaptureClosed(run, m);
        lock (_captureGate)
        {
            _frames.FrameArrived += run.OnFrame;
            _frames.Closed += run.OnClosed;
            run.FramesWired = true;
            run.CaptureStarted = true;
            try { _frames.Start(run.Target); }
            catch (Exception) { return CaptureFailedMessage; }
        }
        try { mixer.Start(); }
        catch (Exception) { return AudioFailedMessage; }

        lock (_gate)
        {
            if (!IsCurrent(run)) return null;                // stopped meanwhile; whoever ended it tears down
            if (slot.Failure is { } early) return early;     // failed right after starting: not a reconnect case
            run.WentLive = true;
            run.LiveSince = _clock.GetTimestamp();
            run.Adapt = new AdaptationController(plan.Rung, plan.StartKbps);
            run.LastDropped = 0;
            run.Tick = _clock.CreateTimer(_ => OnTick(run), null, TickInterval, TickInterval);
            SetStatus(LiveStatus(run));
        }
        Publish();
        return null;
    }

    private string BuildUrl(Platform p, string key)
    {
        var baseUrl = IngestUrlFor(p);
        return baseUrl.EndsWith('/') ? baseUrl + key : baseUrl + "/" + key;
    }

    private Slot CreateSlot(Run run)
    {
        var slot = new Slot(_sessionFactory());
        slot.OnFailed = m => OnSessionFailed(run, slot, m);
        slot.Session.Failed += slot.OnFailed;
        return slot;
    }

    // ------------------------------------------------------------------ Stop / teardown

    /// <summary>Stops streaming and releases everything. Idempotent; safe in any state. Completes after teardown.
    /// A Failed status is left as it is (the run was already torn down).</summary>
    public async Task StopAsync()
    {
        Run? run;
        lock (_gate) run = _run;
        if (run is not null) await EndRunAsync(run, SessionState.Stopped, StoppedMessage).ConfigureAwait(false);
        Task last;
        lock (_gate) last = _lastTeardown;
        await last.ConfigureAwait(false);
    }

    /// <summary>Ends <paramref name="run"/> if it is still current: status first, then teardown. Never throws.</summary>
    private async Task EndRunAsync(Run run, SessionState state, string message)
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            if (!IsCurrent(run)) return;
            _run = null;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _lastTeardown = done.Task;
            SetStatus(new SessionStatus(state, message, null));
        }
        Publish();
        try { await TeardownAsync(run).ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"Teardown failed: {ex.GetType().Name}"); }
        finally { done.TrySetResult(); }
    }

    private async Task TeardownAsync(Run run)
    {
        try { run.Cts.Cancel(); } catch (AggregateException) { /* a cancellation callback threw */ }
        await run.Startup.Task.ConfigureAwait(false);       // Go Live has unwound and registered all it created

        ITimer? tick, hold, retry;
        Slot? session, pending;
        AudioMixer? mixer;
        lock (_gate)
        {
            tick = run.Tick; run.Tick = null;
            hold = run.Hold; run.Hold = null;
            retry = run.Retry; run.Retry = null;
            session = run.Session; run.Session = null;
            pending = run.Pending; run.Pending = null;
            mixer = run.Mixer; run.Mixer = null;
        }
        tick?.Dispose();
        hold?.Dispose();
        retry?.Dispose();

        lock (_captureGate)
        {
            run.CaptureStopped = true;
            if (run.FramesWired)
            {
                _frames.FrameArrived -= run.OnFrame;
                _frames.Closed -= run.OnClosed;
                run.FramesWired = false;
            }
            if (run.CaptureStarted)
            {
                try { _frames.Stop(); } catch (Exception ex) { Debug.WriteLine($"Capture stop failed: {ex.GetType().Name}"); }
            }
        }

        if (mixer is not null)
        {
            if (run.OnMixed is not null) mixer.Mixed -= run.OnMixed;
            if (run.OnNotice is not null) mixer.Notice -= run.OnNotice;
            try { mixer.Dispose(); } catch (Exception ex) { Debug.WriteLine($"Mixer dispose failed: {ex.GetType().Name}"); }
        }
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ adaptation (once a second)

    private void OnTick(Run run)
    {
        if (Interlocked.CompareExchange(ref run.TickBusy, 1, 0) != 0) return;   // a reinit is still in progress
        bool release = true;
        try
        {
            Slot? slot;
            AdaptationController? adapt;
            long liveSince;
            lock (_gate)
            {
                if (!IsCurrent(run) || _status.State != SessionState.Live) return;
                slot = run.Session;
                adapt = run.Adapt;
                liveSince = run.LiveSince;
            }
            if (slot is null || adapt is null) return;

            var h = slot.Session.Health;
            int delta;
            QualityRung? lower;
            lock (_gate)
            {
                if (run.Session != slot || run.Adapt != adapt) return;
                delta = Math.Max(0, h.DroppedFramesTotal - run.LastDropped);
                run.LastDropped = h.DroppedFramesTotal;
                lower = NextLowerRung(run.Plan!);
            }

            var decision = adapt.Decide(new StreamStats(delta, h.SendQueueMs, h.EncodeLagMs, _clock.GetElapsedTime(liveSince)));
            switch (decision)
            {
                case SetBitrate sb:
                    slot.Session.SetBitrate(sb.Kbps);
                    lock (_gate)
                    {
                        if (run.Session == slot && run.Adapt == adapt) run.Plan = run.Plan! with { StartKbps = sb.Kbps };
                    }
                    break;
                case Reinit when lower is not null:
                    release = false;                     // ReinitAsync releases TickBusy when done
                    _ = ReinitAsync(run, slot, adapt, lower);
                    break;
                // Reinit at the lowest rung: nothing lower to go to -> hold.
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Adaptation tick failed: {ex.GetType().Name}");   // timer thread: never throw
        }
        finally
        {
            if (release) Volatile.Write(ref run.TickBusy, 0);
        }
    }

    private async Task ReinitAsync(Run run, Slot slot, AdaptationController adapt, QualityRung lower)
    {
        try
        {
            await slot.Session.ReinitAsync(lower).ConfigureAwait(false);
            lock (_gate)
            {
                if (!IsCurrent(run) || run.Session != slot || run.Adapt != adapt) return;
                // The session clamps the current bitrate into the new rung; mirror that here.
                int kbps = Math.Clamp(run.Plan!.StartKbps, lower.MinKbps, lower.MaxKbps);
                run.Plan = run.Plan with { Rung = lower, StartKbps = kbps };
                run.Adapt = new AdaptationController(lower, kbps);
                if (_status.State == SessionState.Live) SetStatus(LiveStatus(run));
            }
            Publish();
        }
        catch (Exception ex) when (ex is StreamSessionException or OperationCanceledException)
        {
            // The session raised Failed (reconnect logic handles it) or was stopped.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Reinit failed: {ex.GetType().Name}");
        }
        finally
        {
            Volatile.Write(ref run.TickBusy, 0);
        }
    }

    private static QualityRung? NextLowerRung(StreamPlan plan)
    {
        var allowed = plan.Encoder == EncoderKind.OpenH264
            ? QualityPlanner.Ladder.Where(r => r.Name is "720p30" or "480p30").ToList()
            : QualityPlanner.Ladder.ToList();
        int i = allowed.FindIndex(r => r.Name == plan.Rung.Name);
        return i >= 0 && i + 1 < allowed.Count ? allowed[i + 1] : null;
    }

    // ------------------------------------------------------------------ session failure / reconnect

    private static bool IsFatal(string message) =>
        message == ErrorTexts.KeyRejected || message == ErrorTexts.EncoderFailed || message == ErrorTexts.ComponentsMissing;

    private void OnSessionFailed(Run run, Slot slot, string message)
    {
        slot.Failure ??= message;                            // seen by Go Live / retry if they have not adopted it yet
        bool fatal = false;
        lock (_gate)
        {
            if (!IsCurrent(run) || !run.WentLive || run.Session != slot) return;   // stale, or still starting
            if (IsFatal(message)) fatal = true;
            else
            {
                run.Session = null;                          // media stops flowing to the dead session
                run.Adapt = null;
                run.FirstFailure = _clock.GetTimestamp();
                SetStatus(new SessionStatus(SessionState.Reconnecting, ReconnectingMessage, Quality(run.Plan!.Rung)));
                ScheduleRetry(run);
            }
        }
        if (fatal)
        {
            _ = EndRunAsync(run, SessionState.Failed, message);
            return;
        }
        Publish();
        _ = slot.DisposeAsync();
    }

    /// <summary>Under <c>_gate</c>.</summary>
    private void ScheduleRetry(Run run)
    {
        run.Retry?.Dispose();
        run.Retry = _clock.CreateTimer(_ => _ = RetryAsync(run), null, RetryInterval, Timeout.InfiniteTimeSpan);
    }

    private async Task RetryAsync(Run run)
    {
        string url;
        StreamPlan plan;
        lock (_gate)
        {
            if (!IsCurrent(run) || _status.State != SessionState.Reconnecting) return;
            url = run.Url;
            plan = run.Plan!;
        }

        Slot? slot = null;
        string? failure = null;
        try
        {
            slot = CreateSlot(run);
            bool abandoned;
            lock (_gate)
            {
                abandoned = !IsCurrent(run) || _status.State != SessionState.Reconnecting;
                if (!abandoned) run.Pending = slot;
            }
            if (abandoned)
            {
                await slot.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await slot.Session.StartAsync(url, plan, run.Cts.Token).ConfigureAwait(false);
        }
        catch (StreamSessionException ex) { failure = ex.Message; }
        catch (OperationCanceledException) { failure = StoppedMessage; }   // stopped: the run is no longer current
        catch (Exception ex)
        {
            Debug.WriteLine($"Reconnect attempt failed: {ex.GetType().Name}");
            failure = ErrorTexts.Unexpected;
        }

        bool live = false, giveUp = false;
        lock (_gate)
        {
            if (slot is not null && run.Pending == slot) run.Pending = null;
            if (IsCurrent(run) && _status.State == SessionState.Reconnecting)
            {
                failure ??= slot?.Failure;
                if (failure is null)
                {
                    run.Session = slot;
                    run.Adapt = new AdaptationController(plan.Rung, plan.StartKbps);
                    run.LastDropped = 0;
                    SetStatus(LiveStatus(run));
                    live = true;
                }
                else if (IsFatal(failure) || _clock.GetElapsedTime(run.FirstFailure) >= ReconnectDeadline)
                    giveUp = true;
                else
                    ScheduleRetry(run);
            }
        }

        if (!live && slot is not null) await slot.DisposeAsync().ConfigureAwait(false);
        if (giveUp)
        {
            await EndRunAsync(run, SessionState.Failed, IsFatal(failure!) ? failure! : ConnectionNotRestoredMessage)
                .ConfigureAwait(false);
            return;
        }
        if (live)
        {
            Publish();
            RestartCapture(run);
        }
    }

    /// <summary>The new session fails with "no picture" if no frame arrives within 5 s, and WGC only sends frames on
    /// change, so restarting capture makes it deliver a fresh first frame.</summary>
    private void RestartCapture(Run run)
    {
        try
        {
            lock (_captureGate)
            {
                if (run.CaptureStopped) return;
                _frames.Stop();
                run.CaptureStarted = true;
                _frames.Start(run.Target);
            }
        }
        catch (Exception)
        {
            _ = EndRunAsync(run, SessionState.Failed, StoppedPrefix + (run.ClosedMessage ?? CaptureFailedMessage));
        }
    }

    // ------------------------------------------------------------------ capture and audio callbacks

    private void OnFrame(Run run, CaptureFrame f)
    {
        try
        {
            if (!IsCurrent(run)) return;
            run.Session?.Session.PushVideo(f);
            if (run.ClosedMessage is not null) ClearClosedHold(run);
            SampleFrame(run, f);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Frame handling failed: {ex.GetType().Name}");   // capture thread: never throw
        }
    }

    private void SampleFrame(Run run, CaptureFrame f)
    {
        if (Interlocked.CompareExchange(ref run.Sampling, 1, 0) != 0) return;
        try
        {
            long now = _clock.GetTimestamp();
            if (run.HasSampled && _clock.GetElapsedTime(run.LastSample, now) < SampleInterval) return;
            run.HasSampled = true;
            run.LastSample = now;
            var sample = _sampler.Sample(f);
            if (sample is null) return;
            bool blank = run.Blank.Observe(sample, _clock.GetElapsedTime(run.StartTs, now));
            if (blank == run.BlankShown) return;            // racy read is fine: re-checked under the lock
            lock (_gate)
            {
                if (!IsCurrent(run) || run.BlankShown == blank) return;
                run.BlankShown = blank;
                if (_status.State == SessionState.Live) SetStatus(LiveStatus(run));
            }
            Publish();
        }
        finally
        {
            Volatile.Write(ref run.Sampling, 0);
        }
    }

    private void OnCaptureClosed(Run run, string message)
    {
        lock (_gate)
        {
            if (!IsCurrent(run)) return;
            run.ClosedMessage = message;
            if (run.Hold is null)
            {
                int seq = ++run.HoldSeq;
                run.Hold = _clock.CreateTimer(_ => OnHoldExpired(run, seq), null, ClosedHold, Timeout.InfiniteTimeSpan);
            }
            if (_status.State == SessionState.Live) SetStatus(LiveStatus(run));
        }
        Publish();
    }

    private void ClearClosedHold(Run run)
    {
        ITimer? hold;
        lock (_gate)
        {
            if (!IsCurrent(run) || run.ClosedMessage is null) return;
            run.ClosedMessage = null;
            hold = run.Hold;
            run.Hold = null;
            run.HoldSeq++;
            if (_status.State == SessionState.Live) SetStatus(LiveStatus(run));
        }
        hold?.Dispose();
        Publish();
    }

    private void OnHoldExpired(Run run, int seq)
    {
        string? message;
        lock (_gate)
        {
            if (!IsCurrent(run) || run.HoldSeq != seq) return;
            message = run.ClosedMessage;
        }
        if (message is not null) _ = EndRunAsync(run, SessionState.Failed, StoppedPrefix + message);
    }

    private void OnMixed(Run run, AudioChunk c)
    {
        // Runs under the mixer's emit lock: no locks here, and PushAudio only enqueues.
        try
        {
            if (IsCurrent(run)) run.Session?.Session.PushAudio(c);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Audio push failed: {ex.GetType().Name}");
        }
    }

    private void OnMixerNotice(Run run, string notice)
    {
        lock (_gate)
        {
            if (!IsCurrent(run)) return;
            run.BaseMessage = LiveMessage + ". " + notice;   // mic/sound notices last for the rest of the stream
            if (_status.State == SessionState.Live) SetStatus(LiveStatus(run));
        }
        Publish();
    }

    // ------------------------------------------------------------------ status

    private bool IsCurrent(Run run) => ReferenceEquals(_run, run);

    private static string Quality(QualityRung r) => $"{r.Height}p · {r.Fps} fps";

    /// <summary>Under <c>_gate</c>. Capture problems take priority over the black-screen warning.</summary>
    private static SessionStatus LiveStatus(Run r) =>
        new(SessionState.Live, r.ClosedMessage ?? (r.BlankShown ? BlankWarning : r.BaseMessage), Quality(r.Plan!.Rung));

    /// <summary>Under <c>_gate</c>: records the status and queues it for <see cref="Publish"/>.</summary>
    private void SetStatus(SessionStatus s)
    {
        if (s == _status) return;
        _status = s;
        _pending.Enqueue(s);
    }

    /// <summary>Raises queued statuses in order, outside the lock, from one thread at a time. A handler that causes
    /// another status change (e.g. calls StopAsync) has it delivered after it returns.</summary>
    private void Publish()
    {
        lock (_gate)
        {
            if (_raising) return;
            _raising = true;
        }
        while (true)
        {
            SessionStatus s;
            lock (_gate)
            {
                if (_pending.Count == 0) { _raising = false; return; }
                s = _pending.Dequeue();
            }
            try { StatusChanged?.Invoke(s); }
            catch (Exception ex) { Debug.WriteLine($"StatusChanged handler threw: {ex.GetType().Name}"); }
        }
    }

    // ------------------------------------------------------------------ per-run state

    /// <summary>One Go Live attempt and the stream that follows it. Fields are guarded by <c>_gate</c> unless noted.</summary>
    private sealed class Run
    {
        public Run(Platform platform, CaptureTarget target) { Platform = platform; Target = target; }

        public readonly Platform Platform;
        public readonly CaptureTarget Target;
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource Startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long StartTs;

        public string Url = "";                              // contains the key: never logged or shown
        public StreamPlan? Plan;                             // current rung and bitrate (used for reconnects)
        public AudioMixer? Mixer;
        public volatile Slot? Session;                       // receiving media; null while reconnecting (read lock-free)
        public Slot? Pending;                                // being started
        public AdaptationController? Adapt;
        public int LastDropped;
        public long LiveSince;
        public long FirstFailure;
        public bool WentLive;
        public ITimer? Tick, Hold, Retry;
        public int HoldSeq;

        public string BaseMessage = LiveMessage;
        public volatile string? ClosedMessage;               // read lock-free on the frame path
        public bool BlankShown;

        // frame path only (guarded by Sampling)
        public int Sampling;
        public bool HasSampled;
        public long LastSample;
        public readonly BlankFrameDetector Blank = new(BlankThreshold);

        public int TickBusy;                                 // Interlocked

        // guarded by _captureGate
        public bool FramesWired, CaptureStarted, CaptureStopped;

        public Action<CaptureFrame>? OnFrame;
        public Action<string>? OnClosed;
        public Action<AudioChunk>? OnMixed;
        public Action<string>? OnNotice;
    }

    /// <summary>A stream session plus its Failed subscription.</summary>
    private sealed class Slot
    {
        private int _disposed;

        public Slot(IStreamSession session) => Session = session;

        public IStreamSession Session { get; }
        public Action<string>? OnFailed;
        public volatile string? Failure;

        public async Task DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (OnFailed is not null) Session.Failed -= OnFailed;
            try { await Session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"Session dispose failed: {ex.GetType().Name}"); }
        }
    }
}
