namespace Mostlylucid.SignalShingle;

/// <summary>Request-path state. A read never starts materialization.</summary>
public enum SignalShingleState { Warm, Warming }

/// <summary>A renewable consumer lease. Cadence is deliberately not part of a cache key.</summary>
public sealed record SignalShingleDemand(string ConsumerId, TimeSpan RefreshInterval, TimeSpan LeaseDuration)
{
    public static SignalShingleDemand Create(string consumerId, TimeSpan refreshInterval,
        TimeSpan? leaseDuration = null) => new(consumerId, refreshInterval,
        leaseDuration ?? TimeSpan.FromMinutes(2));
}

/// <summary>The cache result intended for an HTTP/Razor request path.</summary>
public sealed record SignalShingleRead<TValue>(SignalShingleState State, TValue? Value,
    long Generation, DateTimeOffset? ProducedAtUtc, TimeSpan? EffectiveRefreshInterval)
{
    public bool IsWarm => State == SignalShingleState.Warm;
}

/// <summary>Work selected by a scheduler; the scheduler owns batching and concurrency.</summary>
public sealed record SignalShingleRefreshCandidate<TKey>(TKey Key, bool IsPinned, bool IsDirty,
    long CurrentGeneration, long DirtyVersion, Guid RefreshLeaseId, int AccessCount,
    DateTimeOffset LastAccessUtc, TimeSpan EffectiveRefreshInterval) where TKey : notnull;

public sealed class SignalShingleCacheOptions
{
    public int Capacity { get; set; } = 256;
    public TimeSpan DefaultRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumStaleness { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}

public sealed record SignalShingleCacheStatistics(int EntryCount, long Reads, long WarmReads,
    long WarmingReads, long Evictions);

/// <summary>
/// A bounded projection cache. Only <see cref="Publish"/> changes the value; callers use
/// <see cref="Read"/> to declare demand and return the latest successful projection.
/// </summary>
public interface ISignalShingleCache<TKey, TValue> where TKey : notnull
{
    SignalShingleRead<TValue> Read(TKey key, SignalShingleDemand? demand = null);
    /// <summary>Publishes an authoritative value without acknowledging a refresh lease.</summary>
    bool Publish(TKey key, TValue value, long generation);
    /// <summary>Marks a resident projection dirty. Returns false when the key is not resident.</summary>
    bool MarkDirty(TKey key);
    void Pin(TKey key, TimeSpan? refreshInterval = null);
    void Unpin(TKey key);
    /// <summary>Atomically reserves due work. Complete or fail each returned lease.</summary>
    IReadOnlyList<SignalShingleRefreshCandidate<TKey>> AcquireRefreshCandidates(int maxCount);
    /// <summary>Publishes a completed reserved refresh. A newer dirty signal remains dirty.</summary>
    bool CompleteRefresh(SignalShingleRefreshCandidate<TKey> candidate, TValue value, long generation);
    /// <summary>Releases a failed refresh lease so a later wave can retry it.</summary>
    void FailRefresh(SignalShingleRefreshCandidate<TKey> candidate);
    SignalShingleCacheStatistics GetStatistics();
}
