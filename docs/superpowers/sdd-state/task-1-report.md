# Task 1 report
Status: DONE. Commit 051145b "feat: models and quality planner".
RED: `dotnet test` failed with CS0246 (EncoderKind, HardwareProfile, NetworkProbeResult not found).
GREEN: Passed 7/7, no warnings.
Files: EasyLive.sln, src/EasyLive.Core/{EasyLive.Core.csproj,Models.cs,QualityPlanner.cs}, tests/EasyLive.Core.Tests/{csproj,QualityPlannerTests.cs}. .gitignore already existed.
Notes: OpenH264 allowed rungs {720p30,480p30}; loss step-down applied after restriction; ladder order highest->lowest. TooSlow = budget < 700 (lowest allowed rung MinKbps); StartKbps = min(budget, rung.Max) truncated to int. Empty Encoders list falls back to OpenH264. Test projects use net8.0-windows10.0.19041.0. Git warns LF->CRLF (harmless).
