# Task 5 report: Platform presets + KeyStore
Status: DONE
Commit: feat: stream key store
RED: `dotnet test tests/EasyLive.Core.Tests` -> compile errors CS0103/CS0246 (Platform, KeyStore, KeyValidator missing).
GREEN: Passed 52, Failed 0, Skipped 2 (pre-existing manual smoke tests: hardware, network), total 54.
Files: src/EasyLive.Core/Platforms.cs (Platform, Platforms, KeyValidator), src/EasyLive.Core/KeyStore.cs, src/EasyLive.Core/EasyLive.Core.csproj (+System.Security.Cryptography.ProtectedData 8.0.0), tests/EasyLive.Core.Tests/KeyStoreTests.cs.
Design: DPAPI CurrentUser with entropy "EasyLive.v1", base64 in JSON dict keyed by platform name; Load returns null on missing/corrupt JSON/bad base64/CryptographicException; Save recovers from a corrupt file by overwriting. Normalize per clarifications (no query stripping).
Tests include corrupt-file, undecryptable-entry, clear, overwrite, file-bytes + base64 plain-key checks; temp dir per test, cleaned in Dispose.
Concerns: none. KeyStore path is caller-supplied (%AppData%\EasyLive default is left to the app layer).

## Fix round 1
Changes:
- KeyStore.ReadEntries swallows only JsonException; IO/UnauthorizedAccess propagate on Save/Clear; Load catches them (plus null JSON value) and returns null. Writes are atomic (path.tmp then File.Move overwrite).
- KeyValidator: server URLs without a key (no path, empty path, "live2"/"rtmp" segment) -> null; one pair of surrounding straight/curly quotes stripped; Unicode Format chars (U+200B/C/D, U+FEFF) -> null.
Tests added: locked file (FileShare.None) makes Save/Clear throw and keeps Facebook entry; null JSON value; no leftover .tmp; server URL cases (YouTube, Facebook, host-only); format chars; quotes; length boundaries 3/4/200/201 (replaced single-row Theory).
Command: dotnet test tests/EasyLive.Core.Tests
Output: Passed! Failed: 0, Passed: 70, Skipped: 2, Total: 72
