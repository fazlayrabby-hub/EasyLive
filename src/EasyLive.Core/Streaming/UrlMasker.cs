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
