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
