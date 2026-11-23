# Task 13 report - installer and user guide

Status: DONE_WITH_CONCERNS.

## Built
- `build/installer/EasyLive.iss`, `build/installer/Read me first.txt`, `build/make-installer.ps1` (runs `publish.ps1` if `artifacts\EasyLive-<v>\EasyLive.exe` is missing, or with `-Rebuild`; then ISCC). Output `artifacts\EasyLive-Setup-1.0.0.exe` (90.2 MB, git-ignored).
- Installer: per-user (`PrivilegesRequired=lowest`) to `%LOCALAPPDATA%\Programs\EasyLive`, no UAC; fixed AppId `{6B0F7C2E-4D3A-4E8B-9C51-3A7E1F5D2B94}` (upgrade in place); CloseApplications (Restart Manager) for a running EasyLive; Start Menu shortcuts (app + "Read me first"); desktop shortcut task checked by default; EasyLive.ico; THIRD-PARTY-NOTICES shown before install, LICENSES + notices + Read me first.txt installed in the app folder; "Launch EasyLive" finish checkbox; publisher Sohan Roni, v1.0.0; uninstaller removes app files and asks whether to also remove `%LOCALAPPDATA%\EasyLive` (default No; silent default No; `/REMOVEDATA=1` removes it silently).
- User guide: `docs/user-guide/EasyLive-User-Guide.md`, `.html` (single file, images and logo embedded), `.pdf` (Edge headless), `images/01..05*.png`, `build-guide.ps1` (md -> html -> pdf, small Markdown subset converter).

## Installed on this PC
- Inno Setup 6.7.4 (official GitHub release `is-6_7_4`, Authenticode valid, Pyrsys B.V.), per-user, `/CURRENTUSER`, at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`. winget failed (source error 0x8a15000f) and jrsoftware.org download URLs returned 404, so the installer was downloaded from GitHub directly.
- Nothing else. The test install in `%TEMP%\el-inst-test` was removed.

## Verified
- Silent install (`/VERYSILENT /DIR=temp /NOICONS /TASKS=""`) exit 0 in ~9 s; 315 MB installed; EasyLive.exe, ffmpeg\ (5 DLLs), LICENSES, Read me first.txt present; uninstall registry entry correct (EasyLive, 1.0.0, Sohan Roni).
- Installed exe started with DOTNET_ROOT removed, title "EasyLive", closed cleanly (exit 0).
- Silent uninstall: folder and registry entry gone; `%LOCALAPPDATA%\EasyLive` kept.
- Screenshots taken from the real app against MediaMTX with fake key testkey-1234, on a plain backdrop window so nothing else shows. The user's `keys.json` was backed up and restored byte-for-byte (347 bytes) after each run.

## Deviations / concerns
- The screenshots show a real app bug: the "What do you want to share?" box displays `CaptureTarget { Title = Monitor 1 ... }` (record ToString) instead of just the title in the closed state. I did not change code. The guide screenshots therefore show it.
- The first live run used the hardware encoder (AMD AMF, 1080p60) on this PC.
- Not verified: the interactive installer wizard (only silent mode run), upgrade-in-place over an older version, the desktop-shortcut and Start Menu shortcut creation (tested with /NOICONS), the uninstall prompt "Yes" path, close-running-app on upgrade, SmartScreen behaviour, and the install on a PC with no .NET. The guide's YouTube/Facebook steps use "look for" wording and were not checked against the live sites. The mute buttons were not shown in action. `%LOCALAPPDATA%\EasyLive\keys.json` already existed on this PC from earlier testing.
- The installer is not code-signed (guide explains the SmartScreen steps).
- The Task 12 legal concerns (OpenH264 patents, FFmpeg source offer) still apply to the installer.

## Refresh (screenshots retaken from the current build)
- The first screenshots came from a stale `artifacts\EasyLive-1.0.0` published before commit 90ed9be (combo showed `CaptureTarget { ... }`, no logo/credit/red live look). The earlier "app bug" concern was therefore not a bug in HEAD.
- Re-ran `build\make-installer.ps1 -Rebuild` (fresh publish from HEAD, licence checks OK), retook all five screenshots with the fake key testkey-1234 on a plain backdrop. Checked each: logo, "by sohan roni", combo shows only "Monitor 1 (1920x1080) - primary", live shot has red background, ON AIR pill and settings card; no personal info. keys.json restored (347 bytes).
- Guide text now mentions the logo/credit, the red window and ON AIR tag (and that they go away after Stop). md/html/pdf regenerated; PDF (12 pages) rendered with Windows.Data.Pdf and pages 4 and 9 inspected: layout fine (one page has white space after a figure kept together with its caption).
- Installer rebuilt (90.3 MB) and the silent install into a temp dir / start without DOTNET_ROOT / silent uninstall test re-run: all passed; user data kept.
