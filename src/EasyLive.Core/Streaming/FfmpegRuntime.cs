using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

/// <summary>
/// One-time FFmpeg setup: locates the DLLs, installs a log callback that writes warnings/errors to
/// <c>%LOCALAPPDATA%\EasyLive\logs\ffmpeg.log</c> (stream keys redacted) and remembers the error lines raised on
/// the current thread so a failed call can be classified (<see cref="ErrorTexts"/>). Never shown to users.
/// </summary>
internal static unsafe class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static readonly object FileGate = new();
    private const long MaxLogBytes = 5 * 1024 * 1024;   // then rotated to ffmpeg.old.log
    private static readonly List<string> Secrets = new();
    private static bool _initialized;
    private static av_log_set_callback_callback? _callback;   // kept alive for the process lifetime

    [ThreadStatic] private static List<string>? _capture;

    public static string LogPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyLive", "logs", "ffmpeg.log");

    /// <summary>The DLL every FFmpeg folder must contain for the bound FFmpeg.AutoGen version.</summary>
    public static string MarkerDll => $"avcodec-{ffmpeg.LibraryVersionMap["avcodec"]}.dll";

    /// <summary>Loads FFmpeg once. False (and logged) if the libraries are missing or unusable.</summary>
    public static bool TryInitialize()
    {
        lock (Gate)
        {
            if (_initialized) return true;
            try
            {
                var dir = FfmpegLocator.Find(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(FfmpegLocator.EnvVar), MarkerDll);
                if (dir is null)
                {
                    Log("init", $"FFmpeg libraries ({MarkerDll}) not found");
                    return false;
                }
                ffmpeg.RootPath = dir;
                string version = ffmpeg.av_version_info();
                _callback = OnLog;
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
                ffmpeg.av_log_set_callback(_callback);
                Log("init", $"FFmpeg {version} loaded from {dir}");
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("init", $"FFmpeg failed to load: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Redact this text (a stream key) from every future log line.</summary>
    public static void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (Secrets) if (!Secrets.Contains(secret)) Secrets.Add(secret);
    }

    /// <summary>Starts collecting FFmpeg error lines raised on THIS thread (until <see cref="EndCapture"/>).</summary>
    public static void BeginCapture() => _capture = new List<string>();

    public static IReadOnlyList<string> EndCapture()
    {
        var lines = _capture ?? new List<string>();
        _capture = null;
        return lines;
    }

    /// <summary>Human-readable FFmpeg error (for the log file only).</summary>
    public static string Describe(int error)
    {
        try
        {
            var buf = stackalloc byte[256];
            ffmpeg.av_strerror(error, buf, 256);
            return $"{Marshal.PtrToStringAnsi((nint)buf)} ({error})";
        }
        catch { return error.ToString(); }
    }

    public static void Log(string source, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {Redact(message.TrimEnd())}";
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxLogBytes) File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // logging must never break streaming
        }
    }

    private static string Redact(string s)
    {
        lock (Secrets)
            foreach (var secret in Secrets)
                s = s.Replace(secret, "***", StringComparison.Ordinal);
        return s;
    }

    private static void OnLog(void* avcl, int level, string format, byte* vl)
    {
        if (level > ffmpeg.AV_LOG_WARNING) return;
        try
        {
            const int size = 1024;
            var buf = stackalloc byte[size];
            int prefix = 1;
            ffmpeg.av_log_format_line(avcl, level, format, vl, buf, size, &prefix);
            string text = Marshal.PtrToStringAnsi((nint)buf) ?? "";
            if (level <= ffmpeg.AV_LOG_ERROR) _capture?.Add(text);
            Log(level <= ffmpeg.AV_LOG_ERROR ? "ffmpeg:error" : "ffmpeg:warn", text);
        }
        catch
        {
            // never throw into native code
        }
    }
}
