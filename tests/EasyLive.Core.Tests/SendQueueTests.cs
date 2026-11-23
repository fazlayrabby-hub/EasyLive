using EasyLive.Core.Streaming;

namespace EasyLive.Core.Tests;

public class SendQueueTests
{
    private readonly List<string> _dropped = new();
    private SendQueue<string> NewQueue(int maxMs = 3000, int? lowWaterMs = null, int ceilingMs = 15000) =>
        new(maxMs, p => _dropped.Add(p), lowWaterMs ?? maxMs * 2 / 3, ceilingMs);

    private static List<string> DrainNames(SendQueue<string> q)
    {
        var all = new List<string>();
        while (q.TryDequeue(out var p, 0)) all.Add(p);
        return all;
    }

    [Fact]
    public void Under_the_limit_nothing_is_dropped_and_order_is_kept()
    {
        var q = NewQueue();
        q.Enqueue("K0", isVideo: true, isKey: true, dtsMs: 0);
        q.Enqueue("A0", isVideo: false, isKey: true, dtsMs: 0);
        q.Enqueue("P33", isVideo: true, isKey: false, dtsMs: 33);
        q.Enqueue("P3000", isVideo: true, isKey: false, dtsMs: 3000);

        Assert.Equal(3000, q.QueueMs);
        Assert.Equal(0, q.DroppedTotal);
        Assert.Equal(new[] { "K0", "A0", "P33", "P3000" }, DrainNames(q));
        Assert.Empty(_dropped);
    }

    [Fact]
    public void Queue_ms_is_newest_minus_oldest_unsent()
    {
        var q = NewQueue();
        Assert.Equal(0, q.QueueMs);
        q.Enqueue("K0", true, true, 100);
        q.Enqueue("A1", false, true, 250);
        q.Enqueue("P2", true, false, 600);
        Assert.Equal(500, q.QueueMs);
        q.TryDequeue(out _, 0);
        Assert.Equal(350, q.QueueMs);
    }

    [Fact]
    public void Over_the_limit_never_drops_audio_or_keyframes()
    {
        var q = NewQueue(maxMs: 100);
        q.Enqueue("K0", true, true, 0);
        q.Enqueue("A0", false, true, 0);
        q.Enqueue("P40", true, false, 40);
        q.Enqueue("A50", false, true, 50);
        q.Enqueue("P80", true, false, 80);
        q.Enqueue("K120", true, true, 120);
        q.Enqueue("A130", false, true, 130);

        var left = DrainNames(q);
        Assert.Equal(new[] { "K0", "A0", "A50", "K120", "A130" }, left);
        Assert.Equal(new[] { "P40", "P80" }, _dropped);
        Assert.Equal(2, q.DroppedTotal);
    }

    [Fact]
    public void Drops_the_oldest_non_key_video_first_and_stops_once_below_low_water()
    {
        // Head is a P frame whose keyframe was already sent: the oldest GOP tail goes first, the newer GOP survives.
        var q = NewQueue(maxMs: 100, lowWaterMs: 90);
        q.Enqueue("P0", true, false, 0);
        q.Enqueue("P10", true, false, 10);
        q.Enqueue("K20", true, true, 20);
        q.Enqueue("P30", true, false, 30);
        q.Enqueue("P110", true, false, 110);   // span 110 > 100 -> drop P0,P10 (up to K20) -> droppable span 80 <= 90

        Assert.Equal(new[] { "P0", "P10" }, _dropped);
        Assert.Equal(2, q.DroppedTotal);
        Assert.Equal(90, q.QueueMs);
        Assert.Equal(new[] { "K20", "P30", "P110" }, DrainNames(q));
    }

    [Fact]
    public void After_a_drop_without_a_following_keyframe_new_delta_frames_are_dropped_until_the_next_keyframe()
    {
        var q = NewQueue(maxMs: 100);
        q.Enqueue("A0", false, true, 0);
        q.Enqueue("P10", true, false, 10);
        q.Enqueue("P200", true, false, 200);   // span 200 -> drop P10,P200; no keyframe follows -> awaiting key
        Assert.True(q.TakeKeyframeRequest());
        Assert.False(q.TakeKeyframeRequest());   // cleared once taken
        Assert.Equal(new[] { "A0" }, DrainNames(q));   // network catches up

        q.Enqueue("P210", true, false, 210);  // would reference the dropped frames -> dropped too
        q.Enqueue("K220", true, true, 220);   // recovery: video resumes at the next keyframe
        q.Enqueue("P230", true, false, 230);

        Assert.Equal(new[] { "P10", "P200", "P210" }, _dropped);
        Assert.Equal(3, q.DroppedTotal);
        Assert.Equal(new[] { "K220", "P230" }, DrainNames(q));
    }

    /// <summary>
    /// Writer that never drains, 30 fps video + ~47 Hz audio. <paramref name="gopFrames"/> = 0 means the encoder
    /// only makes keyframes when the queue asks for one (worst case for request storms); requests are honoured on
    /// the next video frame. Returns the packet times (ms) at which keyframe requests were taken, and the time at
    /// which Enqueue first reported the ceiling (or -1).
    /// </summary>
    private (List<long> Requests, long CeilingAt, int KeysKept) RunNeverDrained(int gopFrames, long untilMs)
    {
        var q = NewQueue(maxMs: 3000, ceilingMs: 15000);
        var requests = new List<long>();
        long ceilingAt = -1;
        bool keyNext = true;
        int frame = 0, keysKept = 0;
        long nextAudio = 0;
        for (long t = 0; t <= untilMs && ceilingAt < 0; t++)
        {
            if (t == nextAudio)
            {
                if (!q.Enqueue("A" + t, false, true, t)) ceilingAt = t;
                nextAudio += 21;
            }
            if (t == frame * 1000L / 30 && ceilingAt < 0)
            {
                bool key = keyNext || (gopFrames > 0 && frame % gopFrames == 0);
                keyNext = false;
                if (key) keysKept++;
                if (!q.Enqueue((key ? "K" : "P") + t, true, key, t)) ceilingAt = t;
                if (q.TakeKeyframeRequest()) { requests.Add(t); keyNext = true; }
                frame++;
            }
        }
        return (requests, ceilingAt, keysKept);
    }

    [Theory]
    [InlineData(0)]    // keyframes only on request
    [InlineData(60)]   // normal 2 s GOP
    public void Never_drained_steady_state_requests_keyframes_at_most_every_2s_and_never_drops_audio(int gop)
    {
        var (requests, ceilingAt, keysKept) = RunNeverDrained(gop, untilMs: 14_000);

        Assert.Equal(-1, ceilingAt);                                   // below the 15 s ceiling
        Assert.DoesNotContain(_dropped, d => d.StartsWith('A'));      // audio never dropped
        Assert.DoesNotContain(_dropped, d => d.StartsWith('K'));      // keyframes never dropped
        Assert.NotEmpty(_dropped);                                     // but video was shed
        for (int i = 1; i < requests.Count; i++)
            Assert.True(requests[i] - requests[i - 1] >= 2000, $"requests at {requests[i - 1]} and {requests[i]} ms");
        Assert.True(keysKept <= 14_000 / 2000 + 2, $"{keysKept} keyframes in 14 s (keyframe storm)");
    }

    [Fact]
    public void Never_drained_queue_trips_the_ceiling_deterministically()
    {
        var (_, ceilingAt, _) = RunNeverDrained(60, untilMs: 20_000);
        Assert.Equal(15_015, ceilingAt);   // first packet whose span (from dts 0) exceeds 15 000 ms: audio at 715 x 21 ms
        Assert.DoesNotContain(_dropped, d => d.StartsWith('A'));
    }

    [Fact]
    public async Task Complete_wakes_a_waiting_consumer_and_drain_returns_the_rest()
    {
        var q = NewQueue();
        q.Enqueue("K0", true, true, 0);
        q.Enqueue("A0", false, true, 0);
        var rest = q.DrainAll();
        Assert.Equal(new[] { "K0", "A0" }, rest);

        var waiter = Task.Run(() => q.TryDequeue(out _, 5000));
        await Task.Delay(50);
        q.Complete();
        Assert.False(await waiter.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, q.QueueMs);
    }
}
