namespace EasyLive.Core.Streaming;

/// <summary>
/// Finds the folder holding the FFmpeg shared DLLs. Search order:
/// 1. <c>&lt;app folder&gt;\ffmpeg</c> (shipped next to the app),
/// 2. the <c>EASYLIVE_FFMPEG_DIR</c> environment variable,
/// 3. during development, <c>tools\ffmpeg\&lt;build&gt;\bin</c> in the app folder or any folder above it.
/// A folder only counts if it contains the marker DLL (e.g. <c>avcodec-62.dll</c>).
/// </summary>
public static class FfmpegLocator
{
    public const string EnvVar = "EASYLIVE_FFMPEG_DIR";

    public static string? Find(string appBaseDir, string? envDir, string markerFile)
    {
        var shipped = Path.Combine(appBaseDir, "ffmpeg");
        if (Has(shipped, markerFile)) return Path.GetFullPath(shipped);

        if (!string.IsNullOrWhiteSpace(envDir) && Has(envDir, markerFile)) return Path.GetFullPath(envDir);

        for (var dir = new DirectoryInfo(Path.GetFullPath(appBaseDir)); dir is not null; dir = dir.Parent)
        {
            var tools = Path.Combine(dir.FullName, "tools", "ffmpeg");
            if (!Directory.Exists(tools)) continue;
            foreach (var build in Directory.GetDirectories(tools).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var bin = Path.Combine(build, "bin");
                if (Has(bin, markerFile)) return Path.GetFullPath(bin);
            }
        }
        return null;
    }

    private static bool Has(string dir, string marker) => File.Exists(Path.Combine(dir, marker));
}
