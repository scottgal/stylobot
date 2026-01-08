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

    public void Reset()
    {
        Signals.Clear();
        CompletedDetectors.Clear();
        FailedDetectors.Clear();
        RanDetectors.Clear();
        AllPolicyDetectors.Clear();
    }
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