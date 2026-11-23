using EasyLive.Core;
using Microsoft.Extensions.Time.Testing;

namespace EasyLive.Core.Tests;

public class NetworkProbeTests
{
    private sealed class FakeTransport : IProbeTransport
    {
        public Queue<object> Connects = new();   // double => success, Exception => failure
        public Func<TimeSpan, CancellationToken, Task<(long, TimeSpan)>> Upload =
            (_, _) => Task.FromResult((12_500_000L, TimeSpan.FromSeconds(2)));

        public Task<double> ConnectMsAsync(string host, int port, CancellationToken ct)
        {
            Assert.Equal(443, port);
            var next = Connects.Dequeue();
            if (next is Exception ex) return Task.FromException<double>(ex);
            return Task.FromResult((double)next);
        }

        public Task<(long bytes, TimeSpan time)> UploadAsync(TimeSpan duration, CancellationToken ct) => Upload(duration, ct);
    }

    private static FakeTransport WithConnects(params object[] c)
    {
        var t = new FakeTransport();
        foreach (var x in c) t.Connects.Enqueue(x);
        return t;
    }

    [Fact]
    public async Task Computes_mean_connect_jitter_and_upload()
    {
        var t = WithConnects(20.0, 22.0, 20.0, 80.0, 20.0);
        var r = await new NetworkProbe(t).RunAsync("ingest.example", CancellationToken.None);
        Assert.Equal(32.4, r.ConnectMs, 6);
        Assert.Equal(31.0, r.JitterMs, 6);
        Assert.Equal(50.0, r.UploadMbps, 6);
        Assert.Equal(0.0, r.LossPct);
    }

    [Fact]
    public async Task Failed_connects_count_as_loss_and_are_excluded()
    {
        var t = WithConnects(20.0, new IOException("x"), 40.0, new IOException("y"), 30.0);
        var r = await new NetworkProbe(t).RunAsync("h", CancellationToken.None);
        Assert.Equal(40.0, r.LossPct, 6);
        Assert.Equal(30.0, r.ConnectMs, 6);
        Assert.Equal(15.0, r.JitterMs, 6);
    }

    [Fact]
    public async Task Throws_Offline_when_all_connects_and_upload_fail()
    {
        var t = WithConnects(new IOException(), new IOException(), new IOException(), new IOException(), new IOException());
        t.Upload = (_, _) => Task.FromException<(long, TimeSpan)>(new HttpRequestException());
        await Assert.ThrowsAsync<OfflineException>(() => new NetworkProbe(t).RunAsync("h", CancellationToken.None));
    }

    [Fact]
    public async Task Connects_failing_but_upload_ok_returns_result()
    {
        var t = WithConnects(new IOException(), new IOException(), new IOException(), new IOException(), new IOException());
        var r = await new NetworkProbe(t).RunAsync("h", CancellationToken.None);
        Assert.Equal(100.0, r.LossPct);
        Assert.Equal(0.0, r.ConnectMs);
        Assert.Equal(50.0, r.UploadMbps, 6);
    }

    [Fact]
    public async Task Upload_failing_but_connects_ok_returns_zero_upload()
    {
        var t = WithConnects(20.0, 20.0, 20.0, 20.0, 20.0);
        t.Upload = (_, _) => Task.FromException<(long, TimeSpan)>(new HttpRequestException());
        var r = await new NetworkProbe(t).RunAsync("h", CancellationToken.None);
        Assert.Equal(0.0, r.UploadMbps);
        Assert.Equal(20.0, r.ConnectMs);
    }

    [Fact]
    public async Task Budget_expiry_during_upload_returns_partial_result()
    {
        var time = new FakeTimeProvider();
        var t = WithConnects(20.0, 20.0, 20.0, 20.0, 20.0);
        var started = new TaskCompletionSource();
        t.Upload = async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return (10_000_000L, TimeSpan.FromSeconds(8));   // bytes sent so far
        };
        var run = new NetworkProbe(t, time).RunAsync("h", CancellationToken.None);
        await started.Task;
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(8));
        var r = await run;
        Assert.Equal(10.0, r.UploadMbps, 6);
        Assert.Equal(20.0, r.ConnectMs);
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        var t = WithConnects(20.0, 20.0, 20.0, 20.0, 20.0);
        using var cts = new CancellationTokenSource();
        t.Upload = async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); return (0L, TimeSpan.Zero); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NetworkProbe(t).RunAsync("h", cts.Token));
    }

    [Fact(Skip = "manual")]
    public async Task Real_network_smoke()
    {
        var r = await new NetworkProbe(new HttpTransport()).RunAsync("a.rtmp.youtube.com", CancellationToken.None);
        Console.WriteLine(r);
        Assert.True(r.UploadMbps > 0);
    }
}
