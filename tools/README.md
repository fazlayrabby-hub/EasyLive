# tools/

Third-party binaries used for development and testing. **Nothing in `tools/ffmpeg/` or `tools/mediamtx/` is
committed** (both are in `.gitignore`); each developer downloads them once.

## FFmpeg (LGPL shared build) — `tools/ffmpeg/`

EasyLive encodes and sends the stream with the FFmpeg libraries through FFmpeg.AutoGen. The FFmpeg DLL
major versions must match the FFmpeg.AutoGen package major (currently **FFmpeg 8.1 ↔ FFmpeg.AutoGen 8.1.0**,
DLLs `avcodec-62.dll`, `avformat-62.dll`, `avutil-60.dll`, `swscale-9.dll`, `swresample-6.dll`).

1. Open https://github.com/BtbN/FFmpeg-Builds/releases (tag `latest`).
2. Download the **LGPL shared** win64 build of the matching release branch, e.g.
   `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`. Do **not** use a `gpl` build (it contains x264/x265 and would
   make the app GPL).
3. Extract it so the DLLs end up in `tools/ffmpeg/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1/bin/`.
4. Check: `bin/ffmpeg.exe -version` must show `--enable-version3` and **no** `--enable-gpl`.

How the app finds the DLLs (`src/EasyLive.Core/Streaming/FfmpegLocator.cs`), first match wins:

1. `<app folder>\ffmpeg\` — this is where the installer puts them;
2. the folder in the `EASYLIVE_FFMPEG_DIR` environment variable;
3. `tools\ffmpeg\<build>\bin\` in the app folder or any folder above it (development).

The same `bin` folder also provides `ffprobe.exe`, which the streaming integration tests use to inspect what
arrived at the test server.

## MediaMTX (MIT) — `tools/mediamtx/`

Local RTMP server used as the test sink by `StreamSessionIntegrationTests`.

1. Open https://github.com/bluenviron/mediamtx/releases (latest).
2. Download `mediamtx_vX.Y.Z_windows_amd64.zip` and extract it so that `tools/mediamtx/mediamtx.exe` exists.

The integration tests start `mediamtx.exe` themselves with their own temporary config (RTMP only, on
`127.0.0.1:1935`; publishing allowed under `test/`, refused elsewhere) and stop it afterwards. If
`mediamtx.exe` or the FFmpeg build is missing, those tests are skipped with a message.
