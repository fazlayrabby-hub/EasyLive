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
        if (key.Length >= 2 && IsQuote(key[0]) && IsQuote(key[^1]))
            key = key[1..^1].Trim();

        var schemeEnd = key.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0 && key[..schemeEnd] is var scheme &&
            (scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase) ||
             scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase)))
        {
            var rest = key[(schemeEnd + 3)..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return null; // no path: just a host
            var path = rest[(slash + 1)..].TrimEnd('/');
            if (path.Length == 0) return null;
            key = path[(path.LastIndexOf('/') + 1)..];
            // A bare server URL ends in the ingest path, not a key.
            if (key.Equals("live2", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("rtmp", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        if (key.Length < MinLength || key.Length > MaxLength) return null;
        foreach (var c in key)
            if (char.IsWhiteSpace(c) || char.IsControl(c) ||
                char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) return null;
        return key;
    }

    public const string ServerAddressHint = "That's the server address - paste the stream key instead (the long code next to it).";
    public const string SpacesHint = "The key can't contain spaces - copy just the stream key.";
    public const string GenericHint = "That doesn't look like a stream key.";

    /// <summary>Plain-language reason a non-empty pasted text cannot be used as a key; null when empty or valid.</summary>
    public static string? Explain(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || Normalize(raw) is not null) return null;
        var text = raw.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0 && text[..schemeEnd] is var scheme &&
            (scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase) || scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase)))
            return ServerAddressHint;
        foreach (var c in text)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return SpacesHint;
        return GenericHint;
    }

    private static bool IsQuote(char c) => c is '"' or '\'' or '\u201C' or '\u201D' or '\u2018' or '\u2019';
}

