namespace EasyLive.Core.Streaming;

/// <summary>
/// Fixed, non-technical texts for <see cref="IStreamSession.Failed"/>. Raw library errors never reach the user;
/// they are written to the log file instead. The classifiers are pure so they can be unit tested.
/// </summary>
public static class ErrorTexts
{
    public const string CouldNotConnect = "Could not connect — check your internet connection.";
    public const string KeyRejected = "The platform did not accept your stream key — paste a new key.";
    public const string ConnectionLost = "The connection to the platform was lost.";
    public const string EncoderFailed = "Your computer could not start the video encoder.";
    public const string NoPicture = "Nothing to stream — the screen did not provide any picture.";
    public const string Unexpected = "Streaming stopped unexpectedly.";
    public const string ComponentsMissing = "Streaming components are missing — please reinstall EasyLive.";

    /// <summary>FFmpeg AVERROR_EOF (FFERRTAG('E','O','F',' ')).</summary>
    internal const int AverrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
    /// <summary>FFmpeg AVERROR_EXIT (FFERRTAG('E','X','I','T')): our interrupt callback aborted (timeout/cancel).</summary>
    internal const int AverrorExit = -('E' | ('X' << 8) | ('I' << 16) | ('T' << 24));

    // Socket errors as FFmpeg reports them on Windows (negated WSA codes) and the POSIX ones.
    private static readonly int[] ClosedByPeer = { AverrorEof, -10054 /*WSAECONNRESET*/, -10053 /*WSAECONNABORTED*/, -104 /*ECONNRESET*/, -32 /*EPIPE*/ };

    private static readonly string[] RejectionWords =
        { "server error", "publish", "unauthorized", "forbidden", "denied", "badname", "not allowed", "authentication" };

    private static readonly string[] TransportWords =
        { "connection to tcp", "failed to resolve", "cannot open connection", "tls", "handshake" };

    /// <summary>
    /// Classifies a failed connect/publish. <paramref name="errorLines"/> are the library's error log lines
    /// emitted during the attempt. A publish refusal from the server, or the server closing the connection
    /// right after the TCP connection succeeded, means the stream key was rejected.
    /// </summary>
    public static string ForConnectFailure(int averror, IEnumerable<string> errorLines)
    {
        var lines = errorLines.Select(l => l.ToLowerInvariant()).ToList();
        if (lines.Any(l => RejectionWords.Any(l.Contains))) return KeyRejected;
        if (lines.Any(l => TransportWords.Any(l.Contains))) return CouldNotConnect;
        return ClosedByPeer.Contains(averror) ? KeyRejected : CouldNotConnect;
    }

    /// <summary>A send failure within the first seconds after publishing is how platforms reject a bad key.</summary>
    public static string ForSendFailure(TimeSpan sincePublish) =>
        sincePublish < TimeSpan.FromSeconds(5) ? KeyRejected : ConnectionLost;
}
