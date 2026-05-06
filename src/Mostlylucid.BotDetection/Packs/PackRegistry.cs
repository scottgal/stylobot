using System.Collections.Immutable;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackRegistry<T> : IEnumerable<T>
{
    private ImmutableArray<T> _items = ImmutableArray<T>.Empty;

    public void Add(T item) => ImmutableInterlocked.Update(ref _items, arr => arr.Add(item));

    public void Clear() => ImmutableInterlocked.Update(ref _items, _ => ImmutableArray<T>.Empty);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
