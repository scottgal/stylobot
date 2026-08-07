namespace Mostlylucid.SignalShingle;

/// <summary>
/// In-process reference implementation. It is intentionally not an <c>IMemoryCache</c>
/// wrapper: retention, demand and materialization eligibility are one coherent policy.
/// </summary>
public sealed class SignalShingleCache<TKey, TValue> : ISignalShingleCache<TKey, TValue>
    where TKey : notnull
{
    private sealed class Entry
    {
        public TValue? Value;
        public bool HasValue;
        public long Generation;
        public DateTimeOffset ProducedAtUtc;
        public DateTimeOffset LastAccessUtc;
        public int AccessCount;
        public bool IsDirty;
        public long DirtyVersion;
        public TimeSpan? PinnedInterval;
        public Guid? RefreshLeaseId;
        public DateTimeOffset RefreshLeaseExpiresAtUtc;
        public Dictionary<string, Lease> Leases { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Lease(TimeSpan Interval, DateTimeOffset ExpiresAtUtc);

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly SignalShingleCacheOptions _options;
    private readonly TimeProvider _time;
    private long _reads, _warmReads, _warmingReads, _evictions;

    public SignalShingleCache(SignalShingleCacheOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new SignalShingleCacheOptions();
        if (_options.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (_options.DefaultRefreshInterval <= TimeSpan.Zero || _options.MaximumStaleness <= TimeSpan.Zero ||
            _options.RefreshLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Refresh and staleness intervals must be positive.");
        _time = timeProvider ?? TimeProvider.System;
    }

    public SignalShingleRead<TValue> Read(TKey key, SignalShingleDemand? demand = null)
    {
        if (demand is { RefreshInterval: var interval } && interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(demand));
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var entry = TryGetOrCreate(key, now);
            _reads++;
            if (entry is null)
            {
                _warmingReads++;
                return new(SignalShingleState.Warming, default, 0, null,
                    demand?.RefreshInterval ?? _options.DefaultRefreshInterval);
            }
            entry.AccessCount++; entry.LastAccessUtc = now;
            if (demand is not null)
            {
                if (string.IsNullOrWhiteSpace(demand.ConsumerId) || demand.LeaseDuration <= TimeSpan.Zero)
                    throw new ArgumentException("Demand needs a consumer id and positive lease duration.", nameof(demand));
                entry.Leases[demand.ConsumerId] = new Lease(demand.RefreshInterval, now + demand.LeaseDuration);
            }
            PruneLeases(entry, now);
            var cadence = EffectiveInterval(entry, now);
            if (entry.HasValue && now - entry.ProducedAtUtc <= _options.MaximumStaleness)
            {
                _warmReads++;
                return new(SignalShingleState.Warm, entry.Value, entry.Generation, entry.ProducedAtUtc, cadence);
            }
            _warmingReads++;
            return new(SignalShingleState.Warming, default, entry.Generation,
                entry.HasValue ? entry.ProducedAtUtc : null, cadence);
        }
    }

    public bool Publish(TKey key, TValue value, long generation)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var entry = TryGetOrCreate(key, now);
            if (entry is null) return false;
            // Prevent an old materialization result from overwriting a newer projection.
            if (entry.HasValue && generation < entry.Generation) return false;
            entry.Value = value; entry.HasValue = true; entry.Generation = generation;
            entry.ProducedAtUtc = now;
            return true;
        }
    }

    public bool MarkDirty(TKey key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return false;
            entry.IsDirty = true;
            entry.DirtyVersion++;
            return true;
        }
    }

    public void Pin(TKey key, TimeSpan? refreshInterval = null)
    {
        var interval = refreshInterval ?? _options.DefaultRefreshInterval;
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(refreshInterval));
        lock (_gate)
        {
            var entry = TryGetOrCreate(key, _time.GetUtcNow())
                ?? throw new InvalidOperationException("Pin capacity exceeds Signal Shingle cache capacity.");
            entry.PinnedInterval = interval;
        }
    }

    public void Unpin(TKey key) { lock (_gate) { if (_entries.TryGetValue(key, out var entry)) entry.PinnedInterval = null; } }

    public IReadOnlyList<SignalShingleRefreshCandidate<TKey>> AcquireRefreshCandidates(int maxCount)
    {
        if (maxCount < 1) return Array.Empty<SignalShingleRefreshCandidate<TKey>>();
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var due = _entries.Select(pair => (Key: pair.Key, Entry: pair.Value)).Where(x =>
            {
                PruneLeases(x.Entry, now);
                var cadence = EffectiveInterval(x.Entry, now);
                var refreshAvailable = x.Entry.RefreshLeaseId is null || x.Entry.RefreshLeaseExpiresAtUtc <= now;
                return refreshAvailable && (x.Entry.PinnedInterval is not null || x.Entry.Leases.Count > 0)
                    ? !x.Entry.HasValue || x.Entry.IsDirty || now - x.Entry.ProducedAtUtc >= cadence
                    : false;
            }).OrderByDescending(x => x.Entry.PinnedInterval is not null)
              .ThenByDescending(x => x.Entry.IsDirty).ThenByDescending(x => x.Entry.AccessCount)
              .ThenByDescending(x => x.Entry.LastAccessUtc).Take(maxCount).ToArray();
            var candidates = due.Select(x =>
            {
                var leaseId = Guid.NewGuid();
                x.Entry.RefreshLeaseId = leaseId;
                x.Entry.RefreshLeaseExpiresAtUtc = now + _options.RefreshLeaseDuration;
                return new SignalShingleRefreshCandidate<TKey>(x.Key, x.Entry.PinnedInterval is not null,
                    x.Entry.IsDirty, x.Entry.Generation, x.Entry.DirtyVersion, leaseId,
                    x.Entry.AccessCount, x.Entry.LastAccessUtc, EffectiveInterval(x.Entry, now));
            }).ToArray();
            return candidates;
        }
    }

    public bool CompleteRefresh(SignalShingleRefreshCandidate<TKey> candidate, TValue value, long generation)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(candidate.Key, out var entry) || entry.RefreshLeaseId != candidate.RefreshLeaseId)
                return false;
            entry.RefreshLeaseId = null;
            if (entry.HasValue && generation < entry.Generation) return false;
            entry.Value = value; entry.HasValue = true; entry.Generation = generation; entry.ProducedAtUtc = now;
            // An invalidation received after acquisition must survive this older completion.
            entry.IsDirty = entry.DirtyVersion != candidate.DirtyVersion;
            return true;
        }
    }

    public void FailRefresh(SignalShingleRefreshCandidate<TKey> candidate)
    {
        lock (_gate)
            if (_entries.TryGetValue(candidate.Key, out var entry) && entry.RefreshLeaseId == candidate.RefreshLeaseId)
                entry.RefreshLeaseId = null;
    }

    public SignalShingleCacheStatistics GetStatistics() { lock (_gate) return new(_entries.Count, _reads, _warmReads, _warmingReads, _evictions); }

    private Entry? TryGetOrCreate(TKey key, DateTimeOffset now)
    {
        if (_entries.TryGetValue(key, out var existing)) return existing;
        if (_entries.Count >= _options.Capacity)
        {
            var victim = _entries.Where(x => x.Value.PinnedInterval is null)
                .OrderBy(x => x.Value.AccessCount).ThenBy(x => x.Value.LastAccessUtc).FirstOrDefault();
            if (!EqualityComparer<KeyValuePair<TKey, Entry>>.Default.Equals(victim, default)) { _entries.Remove(victim.Key); _evictions++; }
            else return null;
        }
        var entry = new Entry { LastAccessUtc = now };
        _entries[key] = entry;
        return entry;
    }

    private static void PruneLeases(Entry entry, DateTimeOffset now)
    {
        foreach (var consumer in entry.Leases.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToArray()) entry.Leases.Remove(consumer);
    }
    private TimeSpan EffectiveInterval(Entry entry, DateTimeOffset now)
    {
        var intervals = entry.Leases.Values.Where(x => x.ExpiresAtUtc > now).Select(x => x.Interval);
        if (entry.PinnedInterval is { } pinned) intervals = intervals.Append(pinned);
        return intervals.DefaultIfEmpty(_options.DefaultRefreshInterval).Min();
    }
}
