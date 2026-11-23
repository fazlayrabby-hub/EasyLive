# Task 10 report: SessionOrchestrator (Go Live state machine)

**Status: DONE_WITH_CONCERNS.** All behaviour from the plan and the T10 rulings is implemented and covered by fast, deterministic unit tests (fakes + `FakeTimeProvider`). The orchestrator has not yet been run end to end against the real `FfmpegStreamSession`, `WgcFrameSource` and WASAPI inputs (that happens in Task 11/12). The concerns are listed in §6.

Branch `feature/easylive-v1`, BASE `036f443`, commits:

| SHA | Subject |
|---|---|
| d999e08 | docs: task 10 rulings in progress ledger |
| a6198a2 | feat: session orchestrator |

## 1. What was built

- `src/EasyLive.Core/SessionOrchestrator.cs` contains `SessionState`, `SessionStatus` and `SessionOrchestrator`. The ctor follows the rulings: `(IAdapterSource, IProbeTransport, IFrameSource, Func<AudioMixer>, Func<IStreamSession>, KeyStore, IFrameSampler, TimeProvider)`. Members: `StatusChanged`, `Status`, `GoLiveAsync`, `StopAsync`, plus public message constants for Task 11.
- `src/EasyLive.Core/Capture/IFrameSampler.cs` contains `IFrameSampler` and `D3D11FrameSampler`. The sampler copies an 8x8 grid of single pixels (`CopySubresourceRegion`, 1x1 boxes at cell centres) into a cached 8x8 staging texture on the texture's own device. It gets the device through a raw `GetDevice` vtable call, as `FrameConverter` does, and turns on multithread protection. It then maps the texture and returns 256 BGRA bytes. It never throws: any exception, a non-BGRA format, MSAA or use after dispose all return null. When the device changes, it recreates the staging texture. It is `IDisposable`.

### Flow
1. **GoLiveAsync** sets Checking ("Checking your computer and internet…"). It then:
   - Loads the key with `KeyStore.Load` and runs it through `KeyValidator.Normalize`. If there is no key: Failed "Paste your stream key first".
   - Runs `HardwareDetector.Detect`, then `NetworkProbe(p, clock).RunAsync(IngestHost)`. `OfflineException` gives Failed "No internet connection".
   - Runs `QualityPlanner.Choose`. TooSlow gives Failed "Your upload speed is too slow to stream".
   - Creates the mixer and subscribes `Notice` before `Start`. Creates the session and subscribes `Failed`, then calls `StartAsync(url, plan)`.
   - Wires `FrameArrived → PushVideo` (plus sampling), `Closed` and `Mixed → PushAudio`, then starts capture and the mixer.
   - Sets Live with message "You're live" and QualityText `"720p · 30 fps"` (`{Height}p · {Fps} fps`).
   - If `StartAsync` throws `StreamSessionException`, the state goes to Failed with its message and no reconnect follows. If the session raises Failed after StartAsync returns but before Live, the result is also Failed directly.
   - Cancelling `ct` during the attempt gives a clean teardown and the Stopped state.
2. **Once a second** (`clock.CreateTimer`), while Live:
   - Builds `StreamStats` from `Health`. `DroppedFramesDelta` is the difference in `DroppedFramesTotal`, never below 0, and resets to 0 for each new session.
   - Passes the stats to `AdaptationController.Decide`. On `SetBitrate` it calls `session.SetBitrate` and remembers the kbps.
   - On `Reinit` it moves to the next lower rung of `QualityPlanner.Ladder` (only 720p30/480p30 for OpenH264) and calls `ReinitAsync`. After that it updates QualityText and creates a new controller at `clamp(current kbps, rung.Min, rung.Max)`. This matches the session's own clamp. In the ruling's example (1500 kbps going to 480p30) the result is 1000, the rung's max.
   - Ignores Reinit at the lowest rung.
   - Ticks are skipped while a reinit is in flight.
3. **Session Failed while Live:**
   - `KeyRejected`, `EncoderFailed` and `ComponentsMissing` tear down at once and give Failed with that message.
   - Any other failure sets Reconnecting ("Reconnecting…", QualityText kept). The session is detached, so media stops flowing to it, and disposed. A retry timer fires every 5 s (one-shot timer, rescheduled after each failed attempt). Each retry uses a NEW session from the factory with the same URL and the current rung and kbps.
   - On success: Live again, then capture restarts (`Stop` + `Start` with the same target) after the new session has started.
   - A fatal message during a retry gives Failed at once.
   - If an attempt is still failing 120 s or more after the first failure, the result is Failed with "The connection could not be restored — the stream has stopped. Check your internet and press Go Live again."
4. **Capture Closed:**
   - The message shows the source's text and the state stays Live.
   - A one-shot 10 s hold timer starts. Repeated `Closed` events update the text but do not restart the timer.
   - Any frame cancels the hold and restores the message.
   - When the hold expires: teardown, then Failed "Stopped — <source message>".
   - If capture cannot restart after a reconnect: Failed "Stopped — <closed message or 'could not be captured'>".
5. **Blank:**
   - The sampler runs at most every 500 ms of clock time. A CAS flag stops overlapping samples. The detector threshold is 3 s.
   - When the detector reports blank, the message becomes the black-screen warning and the state stays Live. It returns to the live message when the frames are no longer black.
   - Priority of messages: capture closed, then black warning, then the base message.
6. **Mixer Notice:** the base live message becomes `"You're live. <notice>"` and the state is unchanged. A notice raised during `mixer.Start()` ("no mic") is kept and shown when the state turns Live. Notices received while Reconnecting are shown on return to Live.
7. **StopAsync** detaches the run and sets Stopped ("Stream stopped"). It then tears everything down: cancel, timers, unsubscribing the capture events and stopping capture, unsubscribing the mixer events and disposing it, unsubscribing and disposing the sessions (live and pending). It completes after teardown. It is idempotent and safe while Checking or Reconnecting. A Failed status is left as it is. Go Live works again afterwards, and GoLive awaits the previous teardown first.

### Design decisions (threading)
- **Per-run object as the generation counter.** Each Go Live creates a `Run`. Every callback (frame, closed, mixed, notice, tick, hold, retry, session Failed) is bound to its run and does nothing unless `ReferenceEquals(_run, run)`. Session callbacks are also bound to their `Slot` (session + handler), so a stale `Failed` from a replaced or stopped session changes nothing. The tests raise stale events on purpose through handlers that were already removed, to model a real session's `Failed` that was already queued on the thread pool.
- **One lock `_gate`** for state and run fields. `StatusChanged` is never raised under it: statuses are queued under the lock and delivered by a pump in order, from one thread at a time. A handler can call `StopAsync` without deadlock, and its status change is delivered after the handler returns (tested).
- **Lock-free media paths.** `OnFrame`/`OnMixed` read the volatile `_run` and `run.Session` and call `PushVideo`/`PushAudio` without any lock. `OnMixed` runs under the mixer's emit lock, so it takes none.
- **Capture Start/Stop and event wiring** are serialised under a separate `_captureGate`, together with a `CaptureStopped` flag. Without this, a reconnect restart racing a Stop could leave capture running. No event handler takes `_captureGate`.
- **Teardown ownership.** Whoever detaches the run (`EndRunAsync`) tears it down. Teardown cancels `run.Cts` and then awaits `run.Startup`, which GoLive completes in its `finally`, so it never races resources that Go Live is still creating. GoLive's own failure paths return a message and end the run after `Startup` is set, so there is no self-deadlock.
- `Slot.DisposeAsync` is idempotent, because teardown and a racing retry attempt may both dispose a pending session.

## 2. Deviations / additions (please confirm)
- **URL join.** The plan says "IngestUrl + key". The YouTube URL `rtmps://a.rtmps.youtube.com:443/live2` has no trailing slash, so the orchestrator inserts `/` when it is missing. Facebook's URL already ends in `/`. Both are tested.
- **`IngestUrlFor` init property** (default `Platforms.IngestUrl`) was added so that Task 11's hidden `--test-url` can point at MediaMTX. It does not change the probe host.
- **The key is re-normalised** with `KeyValidator.Normalize` after Load. A stored garbage value is treated as "no key".
- **Audio/capture start failures** have new plain messages: "Your computer's sound could not be captured." (when `AudioMixer.Start` throws, e.g. no output device for loopback) and "The screen or window you picked could not be captured." These are Failed, not Live without audio. See concern 2.
- **`GoLiveAsync` does not throw** on failure or cancellation. The outcome is reported through `Status`. It throws `InvalidOperationException` only if a stream is already starting or running.
- **`ct` only covers the Go Live attempt.** Cancelling it after Live has no effect; use `StopAsync`.
- **StopAsync after Failed** keeps the Failed status (nothing to tear down).
- **Mixer notices are appended** to the live message ("You're live. Microphone not found — streaming game sound only.") rather than replacing it.

## 3. RED → GREEN
- RED: the test file was written first, and `dotnet build tests/EasyLive.Core.Tests` failed with CS0246 (`SessionStatus`, `SessionOrchestrator`, `IFrameSampler` not found).
- GREEN: the first run had 24/25 passing. The one failure was the fake probe's float rounding (StartKbps 1799 vs 1800), and the assert was loosened to a range. After the extra tests were added, a mojibake problem in the test file's `·`/`—` literals (from my PowerShell edit) was fixed by using `\u` escapes.

## 4. Tests
`tests/EasyLive.Core.Tests/SessionOrchestratorTests.cs`: 27 tests. All use FakeTimeProvider. They have no real sleeps apart from a bounded `WaitFor` poll, which is normally satisfied at once because the timer callbacks run synchronously in `Advance`.
- Checks: no key; offline; too slow.
- Happy path (QualityText, URL, plan, wiring, key not in any status); Facebook URL.
- Mic missing during start shows in the Live message; mixer notice while Live.
- StartAsync failure gives Failed with no retries, and teardown happens. Cancelling via `ct` gives Stopped.
- Congestion for 30 s: SetBitrate(1500), Reinit to 480p30, QualityText "480p · 30 fps", SetBitrate(750) from the new controller.
- Dropped-frames delta rather than total; Reinit at the lowest rung is ignored.
- Failed → Reconnecting → (old session disposed, frames not pushed to it) → retry at 5 s → Live with a new session, same URL and rung, capture restarted.
- Still failing at 120 s → Failed with no further retries; KeyRejected → Failed with no retry; KeyRejected during a retry → Failed.
- Capture closed: the message mentions the window and the state is Live; a frame clears the hold. With no frame for 10 s: Failed "Stopped — …", teardown.
- Blank gives the warning while Live and clears again; the sampler is called at most every 500 ms.
- Stop gives Stopped, teardown and no remaining event subscribers. Stop is idempotent and a second Go Live works.
- Stale Failed after Stop, and from a replaced session after reconnect, is ignored. Stop works while Checking and while Reconnecting.
- Go Live while Live throws. A StatusChanged handler that calls Stop does not deadlock, and the status order is Checking, Live, Stopped.

`tests/EasyLive.Core.Tests/D3D11FrameSamplerTests.cs`: 2 tests, using a real D3D11 device (hardware, falling back to WARP). The first checks that an 8x8 grid read from a half-black/half-white texture gives the expected bytes and that a second call on another texture reuses the cache and is detected as blank. The second checks that the sampler returns null for a null texture and after dispose. They are cheap, so they were not marked Skip.

Command: `dotnet test tests/EasyLive.Core.Tests`. Result: **Passed 195, Skipped 5, Failed 0** (previously 166 + 5 skipped; 29 new tests; the streaming integration tests ran against the local MediaMTX and stayed green). The orchestrator and sampler tests were also run 5 times in a row: green every time, in about 0.3 s. Build: 0 warnings.

## 5. Files changed
- `src/EasyLive.Core/SessionOrchestrator.cs` (new)
- `src/EasyLive.Core/Capture/IFrameSampler.cs` (new; `IFrameSampler` + `D3D11FrameSampler`)
- `tests/EasyLive.Core.Tests/SessionOrchestratorTests.cs` (new)
- `tests/EasyLive.Core.Tests/D3D11FrameSamplerTests.cs` (new)
- `docs/superpowers/sdd-state/progress-ledger.md` (T10 rulings, from the controller)

## 6. Concerns
1. **Not run end to end** with the real `FfmpegStreamSession`, `WgcFrameSource` or `AudioMixer` inputs. In particular, the ruling "restart capture after reconnect so WGC delivers a first frame within the session's 5 s no-picture deadline" is verified only with fakes.
2. **System-audio start failure fails Go Live.** On a PC with no audio output device, WASAPI loopback cannot start and `AudioMixer.Start` throws. I chose Failed ("Your computer's sound could not be captured.") because I did not verify that `FfmpegStreamSession` keeps a valid stream with no audio chunks at all. A reviewer may prefer streaming video with silence. That would need the mixer to tolerate a missing system input; Task 8 has no such mode.
3. **The D3D11FrameSampler Map waits for the GPU on the capture thread** (once per 500 ms, 64 one-pixel copies). This should be negligible, but it is not measured under a game load.
4. **Minimized window → stopped after 10 s.** Per the ruling, the stall monitor's "minimized" `Closed` message starts the same 10 s hold, so a game minimized for more than 10 s ends the stream ("Stopped — Your game is minimized — bring it back to continue."). This follows the ruling but may surprise users.
5. **Stop during a connect can take up to ~12 s,** because `FfmpegStreamSession` connect is not cancellable (deferred T9 minor). Teardown waits for the Go Live attempt to unwind.
6. **The retry cadence is "5 s after the previous attempt ended".** A real failing connect can take up to ~12 s, so real attempts are roughly 17 s apart. The 120 s deadline is checked after each failed attempt, so the final Failed can come up to one connect timeout after 120 s.
7. **StatusChanged runs on whichever thread caused the change** (capture, timer, thread pool). Task 11 must marshal to the UI thread.
