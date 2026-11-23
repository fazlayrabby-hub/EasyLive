# Task 6 report: BlankFrameDetector
Status: DONE
- RED: `dotnet test tests/EasyLive.Core.Tests` -> CS0234 namespace 'EasyLive.Core.Capture' missing.
- GREEN: full `dotnet test`: Passed 83, Failed 0, Skipped 2 (pre-existing manual smoke tests).
- Files: src/EasyLive.Core/Capture/BlankFrameDetector.cs, tests/EasyLive.Core.Tests/BlankFrameDetectorTests.cs (13 tests).
- Namespace EasyLive.Core.Capture. Luma (77R+150G+29B)>>8 <8; empty/<4 byte sample = not blank.
- Concerns: none.
