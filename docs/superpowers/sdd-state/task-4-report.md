# Task 4 report: NetworkProbe
Status: DONE. Commit: feat: network probe.
- RED: tests failed to compile (IProbeTransport missing). GREEN: Passed 31, Skipped 2, Failed 0.
- Real smoke (run once, host a.rtmp.youtube.com, then re-skipped): NetworkProbeResult { UploadMbps = 92.83, JitterMs = 97.99, LossPct = 0, ConnectMs = 180.65 }
- Files: src/EasyLive.Core/NetworkProbe.cs (interface, OfflineException, NetworkProbe, HttpTransport), tests/EasyLive.Core.Tests/NetworkProbeTests.cs, test csproj (+Microsoft.Extensions.TimeProvider.Testing 8.0.0).
- Concerns: brief's mean 28.4 corrected to 32.4 per ruling. Budget expiry during connects skips upload (UploadMbps 0). Offline = all connects fail and upload fails.
