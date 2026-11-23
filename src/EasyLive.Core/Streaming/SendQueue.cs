namespace EasyLive.Core.Streaming;

/// <summary>
/// Bounded queue of muxed packets between the encode thread and the network thread, measured in milliseconds of
/// media (<see cref="QueueMs"/> = newest queued DTS minus oldest unsent DTS).
/// <list type="bullet">
/// <item>Above <c>maxMs</c>, non-key VIDEO packets are dropped, oldest GOP tail first, until the droppable video
/// backlog (newest video DTS minus oldest queued delta frame) is at most <c>lowWaterMs</c>. Audio and keyframes
/// are never dropped; they are not counted in the drop decision, otherwise they would keep it above the limit
/// forever and every new delta frame would be dropped.</item>
/// <item>A delta frame cannot be decoded without the frames before it: a drop removes the rest of that GOP (up to
/// the next queued keyframe). If no keyframe follows, incoming delta frames are dropped until one arrives and a
/// keyframe is requested from the encoder (<see cref="TakeKeyframeRequest"/>), at most once per
/// <c>keyRequestIntervalMs</c> of packet time so congestion cannot turn into a keyframe storm.</item>
/// <item>Above <c>ceilingMs</c> of total span the link is considered lost: <see cref="Enqueue"/> returns false.</item>
/// </list>
/// Thread-safe; one producer and one consumer.
/// </summary>
internal sealed class SendQueue<T>
{
    private readonly record struct Item(T Payload, bool IsVideo, bool IsKey, long DtsMs);

    private readonly int _maxMs;
    private readonly int _lowWaterMs;
    private readonly int _ceilingMs;
    private readonly int _keyRequestIntervalMs;
    private readonly Action<T> _onDropped;
    private readonly LinkedList<Item> _items = new();
    private readonly object _gate = new();
    private bool _awaitingKey;
    private bool _keyRequested;
    private long _lastKeyRequestDts = long.MinValue / 4;
    private long _newestVideoDts;
    private bool _completed;
    private int _dropped;

    /// <param name="onDropped">Called (under the queue lock) for each dropped payload so it can be freed.</param>
    public SendQueue(int maxMs, Action<T> onDropped, int lowWaterMs, int ceilingMs, int keyRequestIntervalMs = 2000)
    {
        _maxMs = maxMs;
        _lowWaterMs = lowWaterMs;
        _ceilingMs = ceilingMs;
        _keyRequestIntervalMs = keyRequestIntervalMs;
        _onDropped = onDropped;
    }

    public int DroppedTotal { get { lock (_gate) return _dropped; } }

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>True after <see cref="Complete"/> once nothing is left to send.</summary>
    public bool IsCompleted { get { lock (_gate) return _completed && _items.Count == 0; } }

    public int QueueMs { get { lock (_gate) return (int)Math.Max(0, SpanMs()); } }

    /// <summary>True once after drops left the stream waiting for a keyframe (then cleared).</summary>
    public bool TakeKeyframeRequest()
    {
        lock (_gate)
        {
            bool r = _keyRequested;
            _keyRequested = false;
            return r;
        }
    }

    /// <summary>Queues a packet (or drops it per the policy). False when the queue exceeded its ceiling: the link
    /// cannot keep up at all and the stream should be treated as lost.</summary>
    public bool Enqueue(T payload, bool isVideo, bool isKey, long dtsMs)
    {
        lock (_gate)
        {
            if (_completed) { _onDropped(payload); return true; }
            if (isVideo)
            {
                _newestVideoDts = Math.Max(_newestVideoDts, dtsMs);
                if (isKey) _awaitingKey = false;
                else if (_awaitingKey)
                {
                    _dropped++;
                    _onDropped(payload);
                    RequestKeyframe(dtsMs);   // again only if the last request is 2 s old
                    return true;
                }
            }
            _items.AddLast(new Item(payload, isVideo, isKey, dtsMs));
            Monitor.PulseAll(_gate);

            if (SpanMs() > _ceilingMs) return false;
            if (SpanMs() > _maxMs)
                while (DroppableVideoSpanMs() > _lowWaterMs && DropOldestGopTail(dtsMs)) { }
            return true;
        }
    }

    /// <summary>Waits up to <paramref name="timeoutMs"/> for a packet. False when timed out or completed and empty.</summary>
    public bool TryDequeue(out T payload, int timeoutMs)
    {
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (_items.Count == 0 && !_completed)
            {
                long left = deadline - Environment.TickCount64;
                if (left <= 0) break;
                Monitor.Wait(_gate, (int)left);
            }
            if (_items.Count == 0) { payload = default!; return false; }
            payload = _items.First!.Value.Payload;
            _items.RemoveFirst();
            return true;
        }
    }

    /// <summary>Stops accepting packets and wakes the consumer. Queued packets stay available.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Removes and returns everything still queued (for freeing at shutdown).</summary>
    public List<T> DrainAll()
    {
        lock (_gate)
        {
            var all = _items.Select(i => i.Payload).ToList();
            _items.Clear();
            return all;
        }
    }

    private long SpanMs() => _items.Count == 0 ? 0 : _items.Last!.Value.DtsMs - _items.First!.Value.DtsMs;

    private long DroppableVideoSpanMs()
    {
        for (var n = _items.First; n is not null; n = n.Next)
            if (n.Value.IsVideo && !n.Value.IsKey) return _newestVideoDts - n.Value.DtsMs;
        return 0;
    }

    private void RequestKeyframe(long nowDts)
    {
        if (nowDts - _lastKeyRequestDts < _keyRequestIntervalMs) return;
        _keyRequested = true;
        _lastKeyRequestDts = nowDts;
    }

    private bool DropOldestGopTail(long nowDts)
    {
        var node = _items.First;
        while (node is not null && !(node.Value.IsVideo && !node.Value.IsKey)) node = node.Next;
        if (node is null) return false;

        bool keyFollows = false;
        while (node is not null)
        {
            var next = node.Next;
            if (node.Value.IsVideo)
            {
                if (node.Value.IsKey) { keyFollows = true; break; }
                _items.Remove(node);
                _dropped++;
                _onDropped(node.Value.Payload);
            }
            node = next;
        }
        if (!keyFollows)
        {
            _awaitingKey = true;
            RequestKeyframe(nowDts);
        }
        return true;
    }
}
