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


# Follow-up: key hint and stream settings card

## `feat: key rejection hint`
- `KeyValidator.Explain(raw)` (Core) returns a plain reason or null: a server address (`rtmp(s)://...`) gives "That's the server address - paste the stream key instead (the long code next to it).", spaces or line breaks give "The key can't contain spaces - copy just the stream key.", too short or too long gives "That doesn't look like a stream key." Empty or valid text gives null.
- `MainViewModel.KeyHint` / `HasKeyHint` show it in amber under the key box. Tests in `KeyHintTests.cs` and `MainViewModelTests.cs`.
- Note: this commit also picked up an uncommitted edit to `docs/superpowers/sdd-state/progress-ledger.md` that was already in the working tree (not mine).

## `feat: stream details panel`
- New `StreamDetails` record (platform, source title, encoder, width/height/fps, current/min/max kbps, measured upload Mbps, dropped frames, send queue ms, elapsed live time). `SessionOrchestrator.DetailsChanged` is raised when the stream goes Live and then on each 1 s tick, from the plan, probe result and `IStreamSession.Health`. `SessionStatus` is unchanged. `ISessionController` gained `DetailsChanged` (the only API change; the test fake was updated). The last details stay available after the stream stops and clear when a new Go Live starts or when the platform or source is changed while idle.
- `MainViewModel.DetailRows` words the values in plain language (encoder names such as "Intel Quick Sync (hardware)", "Software (OpenH264, capped at 720p30)"; audio row follows the mute buttons). Idle shows Platform and Source plus "Quality is chosen automatically when you press Go Live."
- New "Stream settings" card in the window (two-column label/value grid, monospace values). Window height 1010, limited to the screen's work area; the content scrolls when it does not fit.
- Verified by running the app against local MediaMTX (`--test-url rtmp://127.0.0.1:1935/live`) and driving it through UI Automation: monitor picked, key typed, Go Live. Screenshots (live and after Stop) show the Go Live button turned into the pulsing green "YOU'RE LIVE", the green status dot with "1080p · 60 fps", and the settings card filled in: AMD AMF (hardware), 1920 x 1080 at 60 fps, 6,000 kbps (allowed 4,500 to 6,000), 102.3 Mbps measured, keyframes every 2 seconds, 0 dropped, live-for timer counting. After Stop the card keeps the last values. The Stop button showed the red outline when enabled.
- Not verified: the idle screenshot was covered by a Windows Firewall prompt for MediaMTX (the first run on this PC), so the idle card was only checked in the unit tests and partly in that screenshot ("Source: Nothing picked yet" and the quality hint were visible). Stream content was not inspected in VLC or ffprobe this time. The Checking, Reconnecting and Failed dot colors and 125-150% DPI are still unseen.


# Live alert background (commit "feat: red live alert background")
- `MainWindow.xaml`: two overlay layers fade in over 400 ms driven by `State`: crimson gradient (#3B0A14 to #8B1020) with a pulsing bright border while Live, and an amber pulsing layer while Reconnecting. They fade out in 400 ms when the stream ends. A title-bar pill shows a pulsing white dot with "ON AIR" (red) or "RECONNECTING" (amber). The Go Live button keeps its green/cyan "YOU'RE LIVE" look, which stands out well on red. No logic changes.
- Also fixed a bug found in the screenshot: the picker showed "CaptureTarget { Title = ... }" after selecting a source (the custom ComboBox template ignored DisplayMemberPath); it now uses an ItemTemplate.
- Seen on screen (live, via MediaMTX): deep crimson window, translucent red-tinted cards, readable text, the ON AIR pill, a pulsing red border. Reconnecting amber was NOT seen on screen (needs a dropped connection); the 400 ms fade was not observed frame by frame.


# App logo and icon (commit "feat: app logo and icon")
- `build/make-logo.ps1` (System.Drawing only, reproducible) draws a glossy rounded-square badge (cyan, violet, magenta gradient, bevel, top highlight, soft drop shadow) with a white play triangle and two broadcast arcs, and writes `assets/logo.png` (256 px) and `assets/EasyLive.ico` (16, 24, 32, 48, 64, 128, 256 px, PNG entries). Both generated files are committed.
- Wired: `<ApplicationIcon>` (exe icon) and the two assets as WPF resources in `EasyLive.App.csproj` (minimal edit), `Window Icon` in `MainWindow.xaml`, a 24 px logo in the custom title bar replacing the dot, and `SetCurrentProcessExplicitAppUserModelID("EasyLive.App")` at startup so the taskbar groups the window.
- Seen on screen: the logo in the title bar next to EASYLIVE (idle and live) and the logo as the taskbar button icon while the app runs (cropped taskbar screenshot, red running indicator under it). The badge reads clearly at taskbar size.
- Not checked: the exe icon in File Explorer, 16 px rendering in the title bar tray or alt-tab, and the icon at high DPI.


# Title bar credit (commit "feat: title bar credit")
- Added a small "by sohan roni" (10.5 px, muted light blue-grey, 85% opacity) right after EASYLIVE in the custom title bar, bottom-aligned with a 2.5 px lift to sit near the name's baseline. `Window.Title` stays "EasyLive".
- Seen in the live screenshot: legible on the crimson background, not clipped, with clear space before the ON AIR pill at the default 500 px width. An idle screenshot from this run was taken before the window finished drawing, so idle was judged from the earlier idle shots (same bar, navy background). Not tested: a real 480 px minimum-width window (the window is 500 wide by default and the pill sits in the centre with about 50 px of free space either side).
