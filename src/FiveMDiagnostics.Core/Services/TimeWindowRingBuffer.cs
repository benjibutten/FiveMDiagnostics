using System.Collections.Concurrent;

namespace FiveMDiagnostics.Core;

public sealed class TimeWindowRingBuffer<T> where T : class
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly Func<T, DateTimeOffset> _timestampSelector;
    private readonly TimeSpan _retention;

    public TimeWindowRingBuffer(TimeSpan retention, Func<T, DateTimeOffset> timestampSelector)
    {
        _retention = retention;
        _timestampSelector = timestampSelector;
    }

    public int Count => _items.Count;

    public void Add(T item)
    {
        _items.Enqueue(item);
        Trim(_timestampSelector(item) - _retention);
    }

    public IReadOnlyList<T> Snapshot(DateTimeOffset start, DateTimeOffset end)
    {
        return _items
            .Where(item =>
            {
                var timestamp = _timestampSelector(item);
                return timestamp >= start && timestamp <= end;
            })
            .OrderBy(_timestampSelector)
            .ToArray();
    }

    public IReadOnlyList<T> SnapshotAll()
    {
        return _items.OrderBy(_timestampSelector).ToArray();
    }

    private void Trim(DateTimeOffset cutoff)
    {
        while (_items.TryPeek(out var current) && _timestampSelector(current) < cutoff)
        {
            _items.TryDequeue(out _);
        }
    }
}