using System.Collections.Concurrent;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// A small in-memory memory of things just fetched: a search that already fetched a game in full answers the lookup by id that
/// Chronicle makes right after a match. Entries expire after a few minutes and the cache is emptied when it grows large; it holds
/// nothing between runs and is never authoritative. The lifetime and size are technical limits of the plugin's own machinery.
/// </summary>
internal sealed class TtlCache<T>(TimeSpan lifetime, int capacity = 500, Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, T Value)> _items = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public bool TryGet(string key, out T value)
    {
        if (_items.TryGetValue(key, out var hit) && _now() - hit.At < lifetime)
        {
            value = hit.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(string key, T value)
    {
        if (_items.Count >= capacity) _items.Clear();
        _items[key] = (_now(), value);
    }

    public void Clear() => _items.Clear();

    public int Count => _items.Count;
}
