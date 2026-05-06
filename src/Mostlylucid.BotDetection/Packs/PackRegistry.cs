using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackRegistry<T> : IEnumerable<T>
{
    private ConcurrentBag<T> _items = new();

    public void Add(T item) => _items.Add(item);

    public void Clear() => Interlocked.Exchange(ref _items, new ConcurrentBag<T>());

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
