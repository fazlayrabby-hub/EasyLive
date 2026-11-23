# Task 12 report - packaging and licences (Step 5 + file list)

Status: DONE_WITH_CONCERNS (packaging/licences done; manual validation Steps 1-4 not run; legal concerns below).

## What was built
- `build/publish.ps1` (params `-Version` default 1.0.0, `-FfmpegBin`): `dotnet publish src/EasyLive.App -c Release -r win-x64 --self-contained`, explicitly NOT single-file / trimmed / R2R, no PDBs. Output `artifacts/EasyLive-<version>/` and zip `artifacts/EasyLive-<version>.zip` (made with System.IO.Compression, forward-slash entries).
  - Copies into `<out>\ffmpeg\` only `avcodec-62, avformat-62, avutil-60, swscale-9, swresample-6` (plus, automatically, any other FFmpeg DLL they import - none were needed; avfilter/avdevice/ffmpeg.exe/ffprobe.exe/ffplay.exe are not shipped). Proven sufficient: from a copy of the package outside the repo, `FfmpegLocator.Find` returned `...\ffmpeg`, `av_version_info()` = `n8.1.3-39-g660b5aa7db-20261122`, encoders `libopenh264` and `aac` found, `libx264` absent.
  - Copies `LICENSES\` and `THIRD-PARTY-NOTICES.txt`.
  - Verification (fails the script on any hit): ffmpeg.exe `-version` configuration has no `--enable-gpl/--enable-nonfree/--enable-libx264/--enable-libx265`; each shipped DLL scanned for those flags, `x264_encoder`, `x265_encoder`, `x265_api`; the text `libx264`/`libx265` is only allowed as part of the embedded `--disable-libx26x` configuration flag. dumpbin is not installed here, so the script falls back to its own PE import-table scan (prints dependents; only Windows system DLLs + the other FFmpeg DLLs).
  - Informational: avcodec contains the strings "x264 - core %d" / "x264_build" - that is FFmpeg's H.264 parser reading x264's SEI banner, not the encoder.
- `artifacts/` added to `.gitignore`.
- `LICENSES/`: FFmpeg LGPL-3.0 (the `LICENSE.txt` shipped in the BtbN zip), `FFmpeg-SOURCE-AND-RELINK.txt`, `OpenH264-BSD-2-Clause.txt` (from the Cisco repo), `OpenH264-Cisco-BINARY_LICENSE-reference.txt` (from openh264.org, reference only), `OpenH264-NOTICE.txt`, NAudio MIT (nuget cache), FFmpeg.AutoGen MIT (nuget cache), Vortice.Windows / Vortice.Mathematics / SharpGenTools MIT (taken from their GitHub repos because the nupkgs contain no licence file), .NET runtime MIT + its THIRD-PARTY-NOTICES (runtime pack in nuget cache).
- `THIRD-PARTY-NOTICES.txt` summarising each component, version, licence, URL.
- `docs/hardware-validation.md`: "Task 12 validation checklist" with empty results tables.

## Run result
`build/publish.ps1` end to end: OK. Zip `artifacts/EasyLive-1.0.0.zip` = 125.5 MB (folder ~ 170 MB self-contained runtime + ~124 MB FFmpeg DLLs, avcodec 89.5 MB).
Smoke launch of `artifacts\EasyLive-1.0.0\EasyLive.exe` with DOTNET_ROOT unset and removed from PATH: still running after 6 s, main window title "EasyLive", closed gracefully (exit 0). FFmpeg is only loaded when a stream starts, so FFmpeg resolution was checked separately with a tiny console project against a copy of the package (see above).

## Deviations / findings
- FFmpeg.AutoGen 8.1.0 is licensed **MIT** (file `LICENSE.txt` in the package, (c) 2025 Ruslan Balanukhin), not LGPL as the plan/spec assumed. Notices say MIT.
- There is no separate OpenH264 DLL: libopenh264 is statically compiled into `avcodec-62.dll` (BtbN build, `--enable-libopenh264`; `WelsInitEncoderExt`/`ISVCEncoder` present, no openh264 DLL import). The plan's "copy OpenH264 DLL" step therefore does not apply.
- The shipped FFmpeg is v3 LGPL build "n8.1.3-39-g660b5aa7db-20261122" (rolling `latest` BtbN release).
- Self-contained publish includes `Microsoft.Windows.SDK.NET.dll` (24 MB) + `WinRT.Runtime.dll` because the project targets `net8.0-windows10.0.19041.0` (WGC capture).
- Version is 1.0.0 by default (no version property exists in the csproj).

## Licence / legal concerns (please read)
1. **Cisco OpenH264 patent coverage does NOT apply to our copy.** Cisco pays the MPEG LA H.264 royalties only for the binary Cisco builds and hosts, and only if it is downloaded separately to the end user's device (not integrated into third-party software), the user can enable/disable it, and the text "OpenH264 Video Codec provided by Cisco Systems, Inc." is shown. Our OpenH264 is built by BtbN and linked statically into avcodec-62.dll, so none of that holds. We only get the BSD-2 copyright licence, which grants no patent licence. Options (written into `LICENSES/OpenH264-NOTICE.txt`): (a) accept the risk (OpenH264 is only the CPU fallback; QSV/AMF/NVENC are used on most PCs); (b) custom FFmpeg build that loads Cisco's DLL at runtime, downloaded by the app from Cisco on first use with user control and the attribution text; (c) switch the fallback to `h264_mf` (Windows Media Foundation, one-line change in `VideoEncoder.CodecNameFor`); (d) own MPEG LA/Via LA licence or legal advice. Same general H.264/AAC patent caveat applies to the encoders; nothing here is legal advice. I did not add the Cisco attribution string to the app UI, because claiming it would be inaccurate for this copy.
2. **FFmpeg source obligation.** BtbN's `latest` release is rolling; older builds disappear. To be safe the distributor should archive the exact FFmpeg source (commit g660b5aa7db) and the BtbN scripts used, or ship/host them. `FFmpeg-SOURCE-AND-RELINK.txt` gives source URLs, relink/replace instructions (replace DLLs in `ffmpeg\` or set `EASYLIVE_FFMPEG_DIR`) and a written-offer sentence; the "three years" wording is my choice, not taken from a licence text - have it checked.
3. **GPLv3 text not included.** LGPL-3.0 incorporates GPL-3.0 by reference; I only have the LGPL text from the build and did not invent the GPL text (notices link to gnu.org). Add the real file if you want it bundled.
4. **Bundled third-party libraries inside FFmpeg.** The BtbN LGPL build links many libraries (dav1d, libaom, libvpx, opus, vorbis, lame, zimg, libvmaf, libjxl, libass, libbluray, librsvg, libssh, libzmq, gmp, ... see configuration line). Their individual licence texts are not included in the package (the BtbN zip only has FFmpeg's LICENSE.txt). I did not audit them; no GPL ones are expected in the "lgpl" variant (no `--enable-gpl`), but compliance with the notice-reproduction clauses of the BSD-style ones (dav1d, libaom, ...) is NOT done.
5. **Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll**: the nupkg declares its licence only by URL (aka.ms/WinSDKLicenseURL); no text was available offline. The notice states this. Redistribution terms were not verified.
6. Vortice/SharpGen licence texts come from the upstream GitHub repos (the nupkgs only declare "MIT"). EasyLive itself has no licence file/choice yet.

## Not verified
- Steps 1-4 of Task 12 (10-min YouTube runs on Intel + AMD, 3 Mbps clumsy throttle, Facebook run); NVENC untested. Checklist with empty tables added to `docs/hardware-validation.md`.
- Launch on a PC with no .NET installed (this PC has `C:\Program Files\dotnet`; the app is self-contained and did not use DOTNET_ROOT).
- Anti-virus/SmartScreen behaviour of the unsigned zip/exe; installer/code signing not done.
- dumpbin output (not installed; PE import scan used instead).
- Real-stream use of the published build from the package (only a start/close smoke test).
