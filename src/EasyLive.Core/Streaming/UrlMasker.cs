namespace EasyLive.Core.Streaming;

/// <summary>Hides the stream key (everything after the application segment) of an RTMP(S) URL before logging.</summary>
public static class UrlMasker
{
    public static string Mask(string url)
    {
        int keyStart = KeyStart(url);
        return keyStart < 0 ? url : url[..keyStart] + "***";
    }

    /// <summary>The secret part of the URL (stream key, possibly with a query), or null if there is none.</summary>
    /// <summary>Strings to redact from logs: the whole key part, the key before any query, and every query value of at
    /// least <see cref="MinSecretLength"/> characters, each also URL-decoded. Shorter values (like <c>s_bl=1</c>) are
    /// not secret material and redacting them would mangle every log line.</summary>
    public static IReadOnlyList<string> SecretParts(string url)
    {
        var key = KeyPart(url);
        var parts = new List<string>();
        if (key is null) return parts;
        void Add(string s)
        {
            foreach (var v in new[] { s, Decode(s) })
                if (v.Length >= MinSecretLength && !parts.Contains(v)) parts.Add(v);
        }
        Add(key);
        int q = key.IndexOf('?');
        if (q >= 0)
        {
            Add(key[..q]);
            foreach (var pair in key[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq >= 0) Add(pair[(eq + 1)..]);
            }
        }
        return parts;
    }

    public const int MinSecretLength = 4;

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s); }
        catch { return s; }
    }

    public static string? KeyPart(string url)
    {
        int keyStart = KeyStart(url);
        return keyStart < 0 ? null : url[keyStart..];
    }

    // rtmp(s)://host[:port]/app/KEY...  -> index of KEY, or -1.
    private static int KeyStart(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return -1;
        int appSlash = url.IndexOf('/', scheme + 3);
        if (appSlash < 0) return -1;
        int keySlash = url.IndexOf('/', appSlash + 1);
        if (keySlash < 0 || keySlash == url.Length - 1) return -1;
        return keySlash + 1;
    }
}
