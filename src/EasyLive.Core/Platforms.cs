namespace EasyLive.Core;

public enum Platform { YouTube, Facebook }

public static class Platforms
{
    public static string IngestUrl(Platform p) => p switch
    {
        Platform.YouTube => "rtmps://a.rtmps.youtube.com:443/live2",
        Platform.Facebook => "rtmps://live-api-s.facebook.com:443/rtmp/",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Host part of the ingest endpoint (used for network probing).</summary>
    public static string IngestHost(Platform p) => p switch
    {
        Platform.YouTube => "a.rtmps.youtube.com",
        Platform.Facebook => "live-api-s.facebook.com",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
}

public static class KeyValidator
{
    private const int MinLength = 4;
    private const int MaxLength = 200;

    /// <summary>Cleans a pasted stream key; returns null when it cannot be a valid key.</summary>
    public static string? Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var key = raw.Trim();
        if (key.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
        {
            key = key.TrimEnd('/');
            key = key[(key.LastIndexOf('/') + 1)..];
        }

        if (key.Length < MinLength || key.Length > MaxLength) return null;
        foreach (var c in key)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return null;
        return key;
    }
}
