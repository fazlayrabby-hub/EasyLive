using System.Diagnostics;
using System.Net.Sockets;
using EasyLive.Core.Streaming;

namespace EasyLive.Core.Tests.Streaming;

/// <summary>
/// Starts tools/mediamtx/mediamtx.exe (if present) as a local RTMP sink on 127.0.0.1:1935 with a temporary config:
/// RTMP only; anyone may publish under <c>test/</c> and read anything; publishing elsewhere is refused (to
/// exercise the "key rejected" path). Stops it on dispose. <see cref="SkipReason"/> explains what is missing.
/// </summary>
public sealed class MediaMtxFixture : IDisposable
{
    public const string Sink = "rtmp://127.0.0.1:1935/test/";
    public const string DeniedSink = "rtmp://127.0.0.1:1935/denied/";

    private readonly Process? _process;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "easylive-mediamtx-" + Guid.NewGuid().ToString("N"));

    public string? SkipReason { get; }
    public string? FfprobePath { get; }

    public MediaMtxFixture()
    {
        if (!FfmpegRuntime.TryInitialize())
        {
            SkipReason = "FFmpeg LGPL shared DLLs not found (see tools/README.md).";
            return;
        }
        FfprobePath = FindFfprobe();
        if (FfprobePath is null) { SkipReason = "ffprobe.exe not found next to the FFmpeg DLLs (see tools/README.md)."; return; }

        var exe = FindUp(Path.Combine("tools", "mediamtx", "mediamtx.exe"));
        if (exe is null) { SkipReason = "tools/mediamtx/mediamtx.exe not found (see tools/README.md)."; return; }
        if (PortOpen())
        {
            // Debug aid: reuse a sink you started yourself (same config as below) instead of failing.
            if (Environment.GetEnvironmentVariable("EASYLIVE_TEST_USE_RUNNING_SINK") != "1")
                SkipReason = "Port 1935 is already in use by another program; stop it to run the streaming tests.";
            return;
        }

        Directory.CreateDirectory(_dir);
        var yml = Path.Combine(_dir, "mediamtx.yml");
        File.WriteAllText(yml, $"""
            logLevel: info
            logDestinations: [file]
            logFile: {Path.Combine(_dir, "mediamtx.log").Replace('\\', '/')}
            rtsp: false
            rtmp: true
            rtmpAddress: 127.0.0.1:1935
            hls: false
            webrtc: false
            srt: false
            moq: false
            api: false
            authInternalUsers:
              - user: any
                pass:
                ips: []
                permissions:
                  - action: publish
                    path: ~^test/
                  - action: read
                    path:
            paths:
              all_others:
            """);
        _process = Process.Start(new ProcessStartInfo(exe, $"\"{yml}\"")
        {
            WorkingDirectory = _dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        _process!.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var until = DateTime.UtcNow.AddSeconds(10);
        while (!PortOpen())
        {
            if (_process.HasExited || DateTime.UtcNow > until)
            {
                SkipReason = "MediaMTX did not start listening on port 1935 (see " + Path.Combine(_dir, "mediamtx.log") + ").";
                return;
            }
            Thread.Sleep(100);
        }
    }

    public string MediaMtxLog => File.Exists(Path.Combine(_dir, "mediamtx.log")) ? File.ReadAllText(Path.Combine(_dir, "mediamtx.log")) : "";

    private static bool PortOpen()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 1935).Wait(300) && c.Connected;
        }
        catch { return false; }
    }

    private static string? FindFfprobe()
    {
        var dir = FfmpegLocator.Find(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(FfmpegLocator.EnvVar), "ffprobe.exe");
        return dir is null ? null : Path.Combine(dir, "ffprobe.exe");
    }

    internal static string? FindUp(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, relative);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false }) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); }
        }
        catch { }
        _process?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

[CollectionDefinition("mediamtx")]
public sealed class MediaMtxCollection : ICollectionFixture<MediaMtxFixture> { }
