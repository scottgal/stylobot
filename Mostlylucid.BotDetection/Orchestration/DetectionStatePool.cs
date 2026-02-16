using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.ObjectPool;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Pooled state object for bot detection to reduce allocations.
///     Reuses collections across requests to minimize GC pressure.
/// </summary>
internal sealed class PooledDetectionState
{
    public ConcurrentDictionary<string, object> Signals { get; } = new();
    public ConcurrentDictionary<string, bool> CompletedDetectors { get; } = new();
    public ConcurrentDictionary<string, bool> FailedDetectors { get; } = new();
    public HashSet<string> RanDetectors { get; } = new();
    public HashSet<string> AllPolicyDetectors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Zero-allocation read-only set views over the ConcurrentDictionary keys.
    ///     Avoids creating new HashSet copies in BuildState on every wave.
    /// </summary>
    public ConcurrentDictionaryKeySet CompletedDetectorKeys { get; } = new();
    public ConcurrentDictionaryKeySet FailedDetectorKeys { get; } = new();

    public void Reset()
    {
        Signals.Clear();
        CompletedDetectors.Clear();
        FailedDetectors.Clear();
        RanDetectors.Clear();
        AllPolicyDetectors.Clear();
        CompletedDetectorKeys.SetSource(null!);
        FailedDetectorKeys.SetSource(null!);
    }
}

/// <summary>
///     A lightweight IReadOnlySet wrapper over ConcurrentDictionary keys.
///     Avoids allocating a new HashSet every time BuildState is called.
/// </summary>
internal sealed class ConcurrentDictionaryKeySet : IReadOnlySet<string>
{
    private ConcurrentDictionary<string, bool> _source = null!;

    public void SetSource(ConcurrentDictionary<string, bool> source) => _source = source;

    public int Count => _source?.Count ?? 0;
    public bool Contains(string item) => _source?.ContainsKey(item) ?? false;

    public IEnumerator<string> GetEnumerator() =>
        _source?.Keys.GetEnumerator() ?? Enumerable.Empty<string>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsProperSubsetOf(IEnumerable<string> other) => ToHashSet().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => ToHashSet().IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<string> other) => ToHashSet().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => ToHashSet().IsSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => ToHashSet().Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => ToHashSet().SetEquals(other);

    private HashSet<string> ToHashSet() => new(_source?.Keys ?? Enumerable.Empty<string>());
}

/// <summary>
///     Object pool policy for PooledDetectionState.
/// </summary>
internal sealed class DetectionStatePoolPolicy : PooledObjectPolicy<PooledDetectionState>
{
    public override PooledDetectionState Create()
    {
        return new PooledDetectionState();
    }

    public override bool Return(PooledDetectionState obj)
    {
        obj.Reset();
        return true;
    }
}

/// <summary>
///     Factory for creating detection state pools.
/// </summary>
internal static class DetectionStatePoolFactory
{
    private static readonly ObjectPool<PooledDetectionState> Pool =
        new DefaultObjectPool<PooledDetectionState>(new DetectionStatePoolPolicy(), 1024);

    public static ObjectPool<PooledDetectionState> Create()
    {
        return Pool;
    }
}