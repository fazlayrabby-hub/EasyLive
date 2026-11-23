namespace EasyLive.Core.Streaming;

/// <summary>
/// Bounded queue of muxed packets between the encode thread and the network thread, measured in milliseconds of
/// media (newest queued DTS minus oldest unsent DTS). When the span exceeds the limit, non-key VIDEO packets are
/// dropped oldest-first; audio and keyframes are never dropped. Because a delta frame cannot be decoded without
/// the frames before it, a drop removes the whole rest of that GOP (up to the next queued keyframe) and, if no
/// keyframe follows, keeps dropping incoming delta frames until one arrives (and asks the encoder for one via
/// <see cref="TakeKeyframeRequest"/>). Thread-safe; one producer and one consumer.
/// </summary>
internal sealed class SendQueue<T>
{
    private readonly record struct Item(T Payload, bool IsVideo, bool IsKey, long DtsMs);

    private readonly int _maxMs;
    private readonly Action<T> _onDropped;
    private readonly LinkedList<Item> _items = new();
    private readonly object _gate = new();
    private bool _awaitingKey;
    private bool _keyRequested;
    private bool _completed;
    private int _dropped;

    /// <param name="onDropped">Called (under the queue lock) for each dropped payload so it can be freed.</param>
    public SendQueue(int maxMs, Action<T> onDropped)
    {
        _maxMs = maxMs;
        _onDropped = onDropped;
    }

    public int DroppedTotal { get { lock (_gate) return _dropped; } }

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>True after <see cref="Complete"/> once nothing is left to send.</summary>
    public bool IsCompleted { get { lock (_gate) return _completed && _items.Count == 0; } }

    public int QueueMs
    {
        get
        {
            lock (_gate)
                return _items.Count == 0 ? 0 : (int)Math.Max(0, _items.Last!.Value.DtsMs - _items.First!.Value.DtsMs);
        }
    }

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

    public void Enqueue(T payload, bool isVideo, bool isKey, long dtsMs)
    {
        lock (_gate)
        {
            if (_completed) { _onDropped(payload); return; }
            if (isVideo && isKey) _awaitingKey = false;
            if (isVideo && !isKey && _awaitingKey)
            {
                _dropped++;
                _onDropped(payload);
                return;
            }
            _items.AddLast(new Item(payload, isVideo, isKey, dtsMs));
            while (SpanMs() > _maxMs && DropOldestGopTail()) { }
            Monitor.PulseAll(_gate);
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

    private bool DropOldestGopTail()
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
            _keyRequested = true;
        }
        return true;
    }
}
