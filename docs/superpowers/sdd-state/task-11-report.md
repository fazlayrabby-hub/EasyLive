# Task 11 report: WPF app (MainViewModel + window)

**Status: DONE_WITH_CONCERNS.** Builds with 0 warnings; `dotnet test tests/EasyLive.Core.Tests`: Passed 230, Skipped 5, Failed 0 (20 new tests). The interactive run (plan Step 5) was NOT done.

## Built
- `src/EasyLive.App` (net8.0-windows10.0.19041.0, WPF, assembly `EasyLive`), added to `EasyLive.sln`: `App.xaml(.cs)`, `MainWindow.xaml(.cs)`.
  - `App` wires the real parts: `DxgiAdapterSource`, `HttpTransport`, `WgcFrameSource`, `D3D11FrameSampler`, `Func<AudioMixer>` (`LoopbackAudioInput` + `MicrophoneAudioInput.TryCreate()`), `FfmpegStreamSession`, `KeyStore` at `%LOCALAPPDATA%\EasyLive\keys.json`, `TimeProvider.System`. `CaptureTargets.List` feeds the view model.
  - Single instance: `SingleInstance.TryAcquire("EasyLive")`; a second copy shows "EasyLive is already open." and exits.
  - Hidden `--test-url rtmp://host:1935/live` sets `SessionOrchestrator.IngestUrlFor` (the key is appended). The network probe still targets the real platform host.
  - StatusChanged is marshalled with `Dispatcher.BeginInvoke` through `IUiDispatcher`. Unhandled UI exceptions show a plain message. On exit the orchestrator, capture and sampler are stopped and disposed.
  - Window: share picker, YouTube/Facebook toggle, key box with "Where do I find this?" (a plain-text hint box plus the platform page in the browser), big red Go Live, one status line plus quality text, Stop, two mute buttons.
- In `src/EasyLive.Core/App/` (so the existing test project can reach it with no WPF dependency): `MainViewModel`, `RelayCommand`, `SingleInstance`, `ISessionController` and `IUiDispatcher`.
- `SessionOrchestrator` now implements `ISessionController` (one-word change; no behaviour change).

## Deviations / decisions
- **`SingleInstance.TryAcquire(name)` returns bool** (plus `Release(name)`), matching the plan's "returns false the second time".
- **Key save timing.** The orchestrator reads the key from `KeyStore`, so the view model saves the normalized key when Go Live is pressed. If that run never reaches Live (Failed or Stopped first), the previously saved value is restored or cleared. The net effect is "key kept only after a successful start". A failure after having been Live keeps the key.
- Facebook `KeyRejected` is replaced with a text that includes "paste the new key from Facebook Live Producer". YouTube keeps the generic message from `ErrorTexts`.
- Saved key auto-fills when a platform is selected; a first run fills nothing. Switching platform replaces the key box with that platform's saved key, or empty.
- Mute buttons are state in the view model (`MicMuted`, `SystemMuted`, `MuteChanged`). The app applies them to the current mixer and to each new mixer the factory creates.
- Targets are ordered monitors first, then windows. A "Refresh the list" link was added; windows opened after launch need it.
- TDD note: tests were written first, but I ran them only after the implementation existed, so I did not capture a separate RED run.

## Verified
- Solution builds, 0 warnings, 0 errors. The full suite passes (230/5 skipped/0 failed).
- Launch smoke: `EasyLive.exe --test-url rtmp://127.0.0.1:1935/live` ran for 6 s with a window titled "EasyLive". A second launch was blocked by the single-instance guard. Both closed cleanly with `CloseMainWindow`.

## NOT verified (concerns)
- Step 5 by hand: picking a monitor, Go Live to MediaMTX, watching in VLC; Stop; mute buttons on live audio; layout and look; Go Live after Stop. No end-to-end run of the real orchestrator + `FfmpegStreamSession` + WGC + WASAPI exists yet (see task-10 report concern 1).
- Running `EasyLive.exe` requires `DOTNET_ROOT` set to the user-level install on this PC (otherwise the apphost reports ".NET 8 missing"). This is an environment issue, not a code issue. The packaging in Task 12 is self-contained.
- The "Where do I find this?" text is generic ("choose Streaming software and copy the Stream key"); the exact wording of those pages was not checked and may need a tweak.
- Stop and window close can block the UI for up to ~12 s while a connect is in flight (task-10 concern 5); OnExit waits at most 15 s.
- A Go Live that fails before reaching Live and a StopAsync-from-Live both leave the key as described above; a crash mid-run could leave the new key saved even though it never went live.


# Fix round 1 (review findings), commit "fix: stop during go-live wait, key rollback on exit, facebook text"

Fix base: bcf23f9. Tests: Passed 234, Skipped 5, Failed 0 (4 more than before in the view model tests plus 1 orchestrator test); build 0 warnings. Note: as before, I wrote the tests alongside the fix and ran them after, so there is no separate RED run on record.

- **Important 1 (Stop ignored while waiting for the previous teardown).** The view model now creates a CancellationTokenSource per run, passes its token to `GoLiveAsync`, and the Stop handler cancels it before calling `StopAsync`. `SessionOrchestrator.GoLiveAsync` now waits for the previous teardown and pending disposals with `WaitAsync(ct)`; on cancel it sets Stopped ("Stream stopped") and returns, and a later Go Live works. Tests: `Stop_cancels_a_go_live_that_is_still_waiting` (view model), `Cancelling_while_waiting_for_the_previous_teardown_ends_stopped` (orchestrator).
- **Minor 2.** `MainViewModel.Shutdown()` cancels the run and synchronously restores or clears the unverified key if the run never reached Live. `App.OnExit` calls it first. Tests: `Shutdown_restores_the_unverified_key_and_cancels_the_run`, `Shutdown_keeps_the_key_of_a_stream_that_went_live`.
- **Minor 3.** By the time `OnExit` runs the window is already closed (nothing on screen), so I did not add a Hide. `OnExit` now skips disposing capture and the sampler when the stop wait timed out (logs only).
- **Minor 4.** `Apply` captures the run's platform before clearing it. The Facebook test now does a real Go Live with Facebook and asserts the exact text. The YouTube test asserts the exact generic text and no "Facebook".


# UI restyle (commit "style: futuristic UI")

- New `src/EasyLive.App/Themes/Neon.xaml` (merged in App.xaml): palette, gradients (cyan, violet, magenta), glass cards, custom ComboBox / TextBox / pill RadioButton / ghost button / Go Live / Stop styles, status-dot style. `MainWindow.xaml` is now a borderless rounded window with its own title bar (drag, minimize, close). Close calls `Close()`, the same path as before (OnExit then runs Shutdown and the stop wait).
- Go Live is a glowing gradient button; while live it turns green/cyan, reads "YOU'RE LIVE" and its glow pulses. Stop is a magenta/red outline button. The YouTube/Facebook pill uses brand-tinted fills. Focus glow on the key box and picker. The status line has a colored dot: grey idle, amber pulsing for Checking/Reconnecting, green pulsing for Live, red for Failed.
- Only logic change: a read-only `MainViewModel.State` property (raised with the other state notifications) so the dot can follow the session state. All bindings, command names, and automation names were kept; I added `AutomationProperties.Name` on the main controls.
- Layout: window 500x830, min 480x640; the content sits in a vertical ScrollViewer so smaller windows and 125-150% DPI scroll instead of clipping.
- Verified: build 0 warnings, tests 234 passed / 5 skipped, exe launches and shows the window (needs `DOTNET_ROOT` set on this PC), closes cleanly. Screenshots (idle state) taken at 100% DPI and inspected: dark gradient background, rounded glassy cards, neon title, readable text, the pill toggle with a red YouTube fill, the picker, key box and Stop all as intended.
- Not verified: the Live, Checking, Reconnecting and Failed visuals (pulsing glow, status dot colors, "YOU'RE LIVE" text) were not seen on screen because no stream was started; the dropdown popup when open; 125-150% DPI; a focus-glow screenshot; the contrast of the hover states.
