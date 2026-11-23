using System.Net;
using System.Net.Sockets;

namespace EasyLive.Core;

public interface IProbeTransport
{
    /// <summary>Time in ms to open a TCP connection. Throws on failure.</summary>
    Task<double> ConnectMsAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// Uploads to a neutral speed server for up to <paramref name="duration"/>. If <paramref name="ct"/> is
    /// cancelled mid-upload, implementations return the bytes sent so far (and elapsed time) instead of
    /// throwing; the caller decides whether the cancellation was the user's or the probe budget.
    /// Throws on genuine failure.
    /// </summary>
    Task<(long bytes, TimeSpan time)> UploadAsync(TimeSpan duration, CancellationToken ct);
}

public sealed class OfflineException : Exception
{
    public OfflineException() : base("No network connectivity: connect and upload probes both failed.") { }
}

/// <summary>Measures connect latency, jitter, loss and upload speed within an 8 s total budget.</summary>
public sealed class NetworkProbe
{
    private const int ConnectSamples = 5;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UploadDuration = TimeSpan.FromSeconds(4);

    private readonly IProbeTransport _transport;
    private readonly TimeProvider _time;

    public NetworkProbe(IProbeTransport t, TimeProvider? time = null)
    {
        _transport = t;
        _time = time ?? TimeProvider.System;
    }

    public async Task<NetworkProbeResult> RunAsync(string ingestHost, CancellationToken ct)
    {
        using var budget = new CancellationTokenSource(Budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        var samples = new List<double>();
        int failed = 0, attempts = 0;
        for (int i = 0; i < ConnectSamples && !linked.IsCancellationRequested; i++)
        {
            attempts++;
            try { samples.Add(await _transport.ConnectMsAsync(ingestHost, 443, linked.Token)); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { attempts--; }
            catch (Exception) { failed++; }
        }
        ct.ThrowIfCancellationRequested();

        double uploadMbps = 0;
        bool uploadOk = false;
        if (!budget.IsCancellationRequested)
        {
            try
            {
                var (bytes, time) = await _transport.UploadAsync(UploadDuration, linked.Token);
                ct.ThrowIfCancellationRequested();
                if (time > TimeSpan.Zero) uploadMbps = bytes * 8.0 / time.TotalSeconds / 1e6;
                uploadOk = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        if (samples.Count == 0 && !uploadOk) throw new OfflineException();

        double connect = samples.Count > 0 ? samples.Average() : 0;
        double jitter = 0;
        if (samples.Count > 1)
            jitter = samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        double loss = attempts > 0 ? failed * 100.0 / attempts : 0;
        return new NetworkProbeResult(uploadMbps, jitter, loss, connect);
    }
}

/// <summary>Real transport: timed TCP connect, and a streamed POST to Cloudflare's speed endpoint.</summary>
public sealed class HttpTransport : IProbeTransport
{
    private const string UploadUrl = "https://speed.cloudflare.com/__up";

    public async Task<double> ConnectMsAsync(string host, int port, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return sw.Elapsed.TotalMilliseconds;
    }

    public async Task<(long bytes, TimeSpan time)> UploadAsync(TimeSpan duration, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var content = new RandomStreamContent(duration);
        using var req = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = content };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (content.BytesSent > 0)
        {
            // Cancelled mid-upload: report what was sent so far.
        }
        catch (HttpRequestException) when (content.BytesSent > 0 && ct.IsCancellationRequested) { }
        return (content.BytesSent, sw.Elapsed);
    }

    private sealed class RandomStreamContent : HttpContent
    {
        private const int ChunkSize = 64 * 1024;
        private readonly TimeSpan _duration;
        private long _bytes;

        public RandomStreamContent(TimeSpan duration) { _duration = duration; }

        public long BytesSent => Interlocked.Read(ref _bytes);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buf = new byte[ChunkSize];
            Random.Shared.NextBytes(buf);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < _duration)
            {
                await stream.WriteAsync(buf, ct).ConfigureAwait(false);
                Interlocked.Add(ref _bytes, ChunkSize);
            }
        }

        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }
}
