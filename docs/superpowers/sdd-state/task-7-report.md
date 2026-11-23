# Task 7 report: IFrameSource + WgcFrameSource
Status: DONE_WITH_CONCERNS. Commit 5f40212 "feat: Windows Graphics Capture source".

RED: dotnet test failed to compile (CS0246 CaptureStallMonitor / IFrameSource / CaptureFrame / CaptureTarget not found).
GREEN: stall + fake tests 5/5; full Core suite: Passed 88, Skipped 3 (incl. the manual one), Failed 0.

Manual run (temporarily un-skipped, committed version stays Skip), this PC (desktop accessible):
  target: Monitor 1 (1920x1080) - primary   |  Monitor 2 (3840x2160)
  frames=164 size=1920x1080 mismatched=0   -> Passed [3 s]

Files: src/EasyLive.Core/Capture/{IFrameSource,CaptureStallMonitor,CaptureTargets,NativeMethods,WgcFrameSource}.cs,
csproj (+Vortice.Direct3D11 3.8.3), tests: CaptureStallMonitorTests, FakeFrameSource(+Tests), WgcFrameSourceManualTests.
Deviation (as ruled): ListTargets is `CaptureTargets.List()`, not on the interface.

Findings / concerns:
1. WGC delivers frames only when the screen changes. A static desktop gave 8 frames in 3 s (first run). The manual test wiggles the cursor (cursor capture is on) to guarantee activity -> 164 frames. Downstream (Task 9+) must repeat the last frame to hold the target fps on static content.
2. DPI: a DPI-unaware host reports monitor rects virtualized (1536x864 instead of 1920x1080). CaptureTargets.List switches the thread to per-monitor-v2 awareness during enumeration so titles show physical pixels.
3. IsBorderRequired=false is set via manual COM (IGraphicsCaptureSession3, since the 19041 SDK lacks it) in try/catch; not visually verified.
4. Minimized-stall path (CaptureStallMonitor + 500 ms timer) is unit-tested but the timer wiring and window-target capture were not run manually.
5. Texture pointer is valid only during FrameArrived (documented in XML); released after the callback. Stop unsubscribes handlers; an in-flight callback can still finish after Stop returns.

## Fix round 1
Changes (WgcFrameSource.cs, NativeMethods.cs, FrameGeometry.cs, csproj):
- I1: frame handler, Closed (item) handler and stall-timer callback bodies are wrapped in try/catch (Debug.WriteLine); handler re-checks that the pool is still current before touching it.
- I2: Stop/Dispose now detach all fields under _gate (`Teardown`) and dispose pool/session/device outside it; Start's failure cleanup also runs outside the lock.
- I3: new pure `FrameGeometry.ClampToTexture` (RED: CS0103 missing type, then 4/4 green in FrameGeometryTests). Reported Width/Height = min(ContentSize, real D3D11 texture desc).
- I4: the ComImport interface was dropped. Border-off now uses raw vtable calls (QI hr returned, put at slot 7, get at slot 6). The session ABI pointer is taken from `IWinRTObject.NativeObject.ThisPtr`: `MarshalInterface.FromManaged` / `Marshal.QueryInterface` threw InvalidCastException. AllowUnsafeBlocks enabled; InternalsVisibleTo Core.Tests added.
- Minor: surface disposed per frame, `acc` RCW ReleaseComObject'd, interop RCW released; texture wrapper owns the GetInterface ref.
Verified programmatically: manual run prints `border: IsBorderRequired=False (readback)`. Only the visual disappearance of the yellow border is unchecked.
Manual run (un-skipped temporarily, not committed): `dotnet test tests/EasyLive.Core.Tests --filter FullyQualifiedName~WgcFrameSourceManual` -> frames=171 size=1920x1080 mismatched=0, passed.
Full Core suite: Passed 92, Skipped 3, Failed 0.
