# Task 3 report: HardwareDetector

Status: DONE. Commit: see `git log -1` ("feat: hardware detection").

- RED: tests written first; `dotnet test` failed to compile (CS0246 IAdapterSource not found).
- GREEN: `Passed! Failed: 0, Passed: 24, Skipped: 1, Total: 25` (skip = manual smoke test).
- Impl: src/EasyLive.Core/HardwareDetector.cs (IAdapterSource, DxgiAdapterSource via Vortice.DXGI 3.8.3 MIT, HardwareDetector). Added PackageReference to EasyLive.Core.csproj.
- Real Detect() output (smoke test temporarily un-skipped, not committed):
  Adapters: Intel(R) Iris(R) Xe Graphics | Microsoft Basic Render Driver
  GPU=Intel(R) Iris(R) Xe Graphics Encoders=[QuickSync, OpenH264] Cores=16
- Files: src/EasyLive.Core/HardwareDetector.cs, EasyLive.Core.csproj, tests/EasyLive.Core.Tests/HardwareDetectorTests.cs, docs/hardware-validation.md.
- Concerns: DXGI failure is swallowed to empty list (-> Unknown/OpenH264). Vortice.DXGI pulls transitive Vortice deps (MIT).
