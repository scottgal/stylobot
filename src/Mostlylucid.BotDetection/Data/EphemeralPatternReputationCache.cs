using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Pattern reputation cache using ephemeral signal patterns.
///     Key features over InMemoryPatternReputationCache:
///     - Uses ephemeral SignalSink for observability of reputation changes
///     - Hot key tracking: frequently accessed patterns get extended lifetime
///     - LRU-style eviction with signal-based monitoring
///     - Built-in decay via ephemeral coordinator background processing
///     - Progress signals for decay sweeps and GC
/// </summary>
public sealed class EphemeralPatternReputationCache : IPatternReputationCache, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    // Background decay coordinator
    private readonly EphemeralWorkCoordinator<DecayWork> _decayCoordinator;

    // Configuration
    private readonly int _hotAccessThreshold;
    private readonly TimeSpan _hotKeyExtension;
    private readonly ILogger<EphemeralPatternReputationCache> _logger;
    private readonly int _maxPatterns;
    private readonly ILearnedPatternStore? _patternStore;

    // Background persistence coordinator for batched SQLite writes
    private readonly EphemeralWorkCoordinator<PersistWork>? _persistCoordinator;

    // Ephemeral signal sink for reputation change observability
    private readonly SignalSink _signals;
    private readonly PatternReputationUpdater _updater;
    private int _accessCount;

    // Tracking
    private DateTimeOffset _lastDecaySweep = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastGc = DateTimeOffset.UtcNow;

    public EphemeralPatternReputationCache(
        ILogger<EphemeralPatternReputationCache> logger,
        PatternReputationUpdater updater,
        ILearnedPatternStore? patternStore = null,
        int maxPatterns = 10000,
        int hotAccessThreshold = 10,
        TimeSpan? hotKeyExtension = null)
    {
        _logger = logger;
        _updater = updater;
        _patternStore = patternStore;
        _maxPatterns = maxPatterns;
        _hotAccessThreshold = hotAccessThreshold;
        _hotKeyExtension = hotKeyExtension ?? TimeSpan.FromHours(24);

        // Global signal sink for reputation changes
        _signals = new SignalSink(
            5000,
            TimeSpan.FromMinutes(30));

        // Background decay coordinator - single-threaded sequential processing
        _decayCoordinator = new EphemeralWorkCoordinator<DecayWork>(
            async (work, ct) =>
            {
                switch (work.Type)
                {
                    case DecayWorkType.DecaySweep:
                        await ExecuteDecaySweepAsync(ct);
                        break;
                    case DecayWorkType.GarbageCollect:
                        await ExecuteGarbageCollectAsync(ct);
                        break;
                    case DecayWorkType.EvictCold:
                        EvictColdestPatterns(work.Count);
                        break;
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1, // Single background thread
                MaxTrackedOperations = 100,
                Signals = _signals,
                OnSignal = evt => { } // Just for tracking
            });

        // Background persistence coordinator for batched SQLite writes (avoids file locks)
        if (_patternStore != null)
            _persistCoordinator = new EphemeralWorkCoordinator<PersistWork>(
                async (work, ct) => { await ExecutePersistBatchAsync(work.Reputations, ct); },
                new EphemeralOptions
                {
                    MaxConcurrency = 1, // Single writer to avoid SQLite locks
                    MaxTrackedOperations = 50,
                    Signals = _signals,
                    OnSignal = evt => { }
                });
    }

    /// <summary>
    ///     Check if any pattern recently changed state.
    /// </summary>
    public bool HasRecentStateChanges =>
        _signals.Detect(evt => evt.StartsWith(ReputationSignals.StateChanged));

    public async ValueTask DisposeAsync()
    {
        _decayCoordinator.Complete();
        await _decayCoordinator.DisposeAsync();

        if (_persistCoordinator != null)
        {
            _persistCoordinator.Complete();
            await _persistCoordinator.DisposeAsync();
        }
    }

    public PatternReputation? Get(string patternId)
    {
        if (!_cache.TryGetValue(patternId, out var entry))
            return null;

        // Track access for hot key detection
        entry.AccessCount++;
        entry.LastAccess = DateTimeOffset.UtcNow;

        // Hot key? Extend lifetime and emit signal
        if (entry.AccessCount == _hotAccessThreshold)
        {
            entry.HotUntil = DateTimeOffset.UtcNow + _hotKeyExtension;

            if (ShouldSample())
                _signals.Raise($"{ReputationSignals.HotKey}:{patternId}:{entry.Reputation.PatternType}");
        }

        return entry.Reputation;
    }

    public PatternReputation GetOrCreate(string patternId, string patternType, string pattern)
    {
        var entry = _cache.GetOrAdd(patternId, _ =>
        {
            var reputation = new PatternReputation
            {
                PatternId = patternId,
                PatternType = patternType,
                Pattern = pattern,
                BotScore = 0.5,
                Support = 0,
                State = ReputationState.Neutral,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
                StateChangedAt = DateTimeOffset.UtcNow
            };

            if (ShouldSample()) _signals.Raise($"{ReputationSignals.PatternCreated}:{patternId}:{patternType}");

            return new CacheEntry(reputation);
        });

        // Track access
        entry.AccessCount++;
        entry.LastAccess = DateTimeOffset.UtcNow;

        // Enforce max size - schedule eviction in background
        if (_cache.Count > _maxPatterns)
            _ = _decayCoordinator.TryEnqueue(new DecayWork(DecayWorkType.EvictCold, _cache.Count - _maxPatterns));

        return entry.Reputation;
    }

    public void Update(PatternReputation reputation)
    {
        var oldState = _cache.TryGetValue(reputation.PatternId, out var existing)
            ? existing.Reputation.State
            : ReputationState.Neutral;

        var entry = _cache.AddOrUpdate(
            reputation.PatternId,
            _ => new CacheEntry(reputation),
            (_, existing) =>
            {
                existing.Reputation = reputation;
                existing.LastAccess = DateTimeOffset.UtcNow;
                existing.IsDirty = true; // Mark as needing persistence
                return existing;
            });

        // Emit signals for updates
        if (ShouldSample())
            _signals.Raise($"{ReputationSignals.PatternUpdated}:{reputation.PatternId}:{reputation.BotScore:F2}");

        // State change is always signaled (important for monitoring)
        if (reputation.State != oldState)
        {
            _signals.Raise($"{ReputationSignals.StateChanged}:{reputation.PatternId}:{oldState}:{reputation.State}");

            _logger.LogInformation(
                "Pattern {PatternId} state changed: {OldState} → {NewState} (score={Score:F2})",
                reputation.PatternId, oldState, reputation.State, reputation.BotScore);
        }
    }

    public IEnumerable<PatternReputation> GetByType(string patternType)
    {
        return _cache.Values
            .Where(e => e.Reputation.PatternType == patternType)
            .Select(e => e.Reputation);
    }

    public IEnumerable<PatternReputation> GetByState(ReputationState state)
    {
        return _cache.Values
            .Where(e => e.Reputation.State == state)
            .Select(e => e.Reputation);
    }

    public Task DecaySweepAsync(CancellationToken ct = default)
    {
        // Queue decay sweep for background processing
        return _decayCoordinator.EnqueueAsync(new DecayWork(DecayWorkType.DecaySweep), ct).AsTask();
    }

    public Task GarbageCollectAsync(CancellationToken ct = default)
    {
        // Queue GC for background processing
        return _decayCoordinator.EnqueueAsync(new DecayWork(DecayWorkType.GarbageCollect), ct).AsTask();
    }

    public async Task PersistAsync(CancellationToken ct = default)
    {
        if (_patternStore == null || _persistCoordinator == null)
            return;

        try
        {
            // Only persist patterns that have changed since last persist (dirty patterns)
            var dirtyPatterns = _cache.Values
                .Where(e => e.IsDirty)
                .Select(e => e.Reputation)
                .ToList();

            if (dirtyPatterns.Count > 0) await _persistCoordinator.EnqueueAsync(new PersistWork(dirtyPatterns), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue patterns for persistence");
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_patternStore == null)
            return;

        try
        {
            // Load all signatures from SQLite
            var signatures = await _patternStore.GetByConfidenceAsync(0.0, ct);
            var loaded = 0;

            foreach (var signature in signatures)
            {
                if (ct.IsCancellationRequested) break;

                // Convert LearnedSignature to PatternReputation
                // Upgrade LogOnly patterns based on confidence (they may have been saved as Neutral initially)
                var state = signature.Action switch
                {
                    LearnedPatternAction.Full when signature.Confidence >= 0.9 => ReputationState.ConfirmedBad,
                    LearnedPatternAction.Full => ReputationState.Suspect,
                    LearnedPatternAction.ScoreOnly when signature.Confidence >= 0.6 => ReputationState.Suspect,
                    LearnedPatternAction.LogOnly when signature.Confidence >= 0.9 => ReputationState.ConfirmedBad,
                    LearnedPatternAction.LogOnly when signature.Confidence >= 0.6 => ReputationState.Suspect,
                    LearnedPatternAction.LogOnly when signature.Confidence <= 0.1 => ReputationState.ConfirmedGood,
                    _ => ReputationState.Neutral
                };

                var reputation = new PatternReputation
                {
                    PatternId = signature.PatternId,
                    PatternType = signature.SignatureType,
                    Pattern = signature.Pattern,
                    BotScore = signature.Confidence,
                    Support = signature.Occurrences,
                    State = state,
                    FirstSeen = signature.FirstSeen,
                    LastSeen = signature.LastSeen,
                    StateChangedAt = signature.LastSeen,
                    IsManual = false,
                    Notes = null
                };

                _cache[signature.PatternId] =
                    new CacheEntry(reputation, false); // Loaded from DB, no need to persist immediately
                loaded++;
            }

            if (loaded > 0) _logger.LogInformation("Loaded {Count} pattern reputations from SQLite", loaded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pattern reputations");
        }
    }

    public ReputationCacheStats GetStats()
    {
        var patterns = _cache.Values.ToList();
        var now = DateTimeOffset.UtcNow;

        return new ReputationCacheStats
        {
            TotalPatterns = patterns.Count,
            NeutralCount = patterns.Count(p => p.Reputation.State == ReputationState.Neutral),
            SuspectCount = patterns.Count(p => p.Reputation.State == ReputationState.Suspect),
            ConfirmedBadCount = patterns.Count(p => p.Reputation.State == ReputationState.ConfirmedBad),
            ConfirmedGoodCount = patterns.Count(p => p.Reputation.State == ReputationState.ConfirmedGood),
            ManualBlockedCount = patterns.Count(p => p.Reputation.State == ReputationState.ManuallyBlocked),
            ManualAllowedCount = patterns.Count(p => p.Reputation.State == ReputationState.ManuallyAllowed),
            GcEligibleCount = patterns.Count(p => _updater.IsEligibleForGc(p.Reputation)),
            AverageBotScore = patterns.Count > 0 ? patterns.Average(p => p.Reputation.BotScore) : 0.5,
            AverageSupport = patterns.Count > 0 ? patterns.Average(p => p.Reputation.Support) : 0,
            OldestPattern = patterns.MinBy(p => p.Reputation.FirstSeen)?.Reputation.FirstSeen,
            NewestPattern = patterns.MaxBy(p => p.Reputation.LastSeen)?.Reputation.LastSeen,
            LastDecaySweep = _lastDecaySweep,
            LastGc = _lastGc
        };
    }

    /// <summary>
    ///     Get recent reputation change signals for observability.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetRecentSignals()
    {
        return _signals.Sense();
    }

    /// <summary>
    ///     Get signals matching a pattern (e.g., "reputation.state_changed:*").
    /// </summary>
    public IReadOnlyList<SignalEvent> GetSignals(Func<SignalEvent, bool> predicate)
    {
        return _signals.Sense(predicate);
    }

    private Task ExecuteDecaySweepAsync(CancellationToken ct)
    {
        _signals.Raise($"{ReputationSignals.DecaySweepStarted}:{_cache.Count}");

        var updated = 0;
        var stateChanges = 0;

        foreach (var (id, entry) in _cache.ToArray())
        {
            if (ct.IsCancellationRequested) break;

            var oldState = entry.Reputation.State;
            var decayed = _updater.ApplyTimeDecay(entry.Reputation);

            if (decayed != entry.Reputation)
            {
                entry.Reputation = decayed;
                entry.IsDirty = true; // Mark as needing persistence after decay
                updated++;

                if (decayed.State != oldState)
                {
                    stateChanges++;
                    _signals.Raise($"{ReputationSignals.StateChanged}:{id}:{oldState}:{decayed.State}");
                }
            }
        }

        _lastDecaySweep = DateTimeOffset.UtcNow;

        _signals.Raise($"{ReputationSignals.DecaySweepCompleted}:{updated}:{stateChanges}");

        if (updated > 0)
            _logger.LogDebug("Decay sweep updated {Count} patterns, {StateChanges} state changes", updated,
                stateChanges);

        return Task.CompletedTask;
    }

    private Task ExecuteGarbageCollectAsync(CancellationToken ct)
    {
        _signals.Raise($"{ReputationSignals.GcStarted}:{_cache.Count}");

        var removed = 0;

        foreach (var (id, entry) in _cache.ToArray())
        {
            if (ct.IsCancellationRequested) break;

            // Hot keys survive GC
            if (entry.HotUntil > DateTimeOffset.UtcNow)
                continue;

            if (_updater.IsEligibleForGc(entry.Reputation))
                if (_cache.TryRemove(id, out _))
                {
                    removed++;
                    _signals.Raise($"{ReputationSignals.Evicted}:{id}:gc");
                }
        }

        _lastGc = DateTimeOffset.UtcNow;

        _signals.Raise($"{ReputationSignals.GcCompleted}:{removed}");

        if (removed > 0) _logger.LogInformation("Garbage collected {Count} stale patterns", removed);

        return Task.CompletedTask;
    }

    private void EvictColdestPatterns(int count)
    {
        // Get coldest patterns (low access count, oldest last access, not hot)
        var coldest = _cache
            .Where(e => e.Value.HotUntil < DateTimeOffset.UtcNow) // Not hot
            .Where(e => !e.Value.Reputation.IsManual) // Not manual
            .Where(e => e.Value.Reputation.State == ReputationState.Neutral) // Only neutral
            .OrderBy(e => e.Value.AccessCount)
            .ThenBy(e => e.Value.LastAccess)
            .Take(count)
            .Select(e => e.Key)
            .ToList();

        foreach (var key in coldest)
            if (_cache.TryRemove(key, out _))
                _signals.Raise($"{ReputationSignals.Evicted}:{key}:cold");

        if (coldest.Count > 0) _logger.LogDebug("Evicted {Count} cold patterns", coldest.Count);
    }

    private async Task ExecutePersistBatchAsync(IReadOnlyList<PatternReputation> reputations, CancellationToken ct)
    {
        try
        {
            var saved = 0;

            foreach (var reputation in reputations)
            {
                if (ct.IsCancellationRequested) break;

                // Convert PatternReputation to LearnedSignature
                var signature = new LearnedSignature
                {
                    PatternId = reputation.PatternId,
                    SignatureType = reputation.PatternType,
                    Pattern = reputation.Pattern,
                    Confidence = reputation.BotScore,
                    Occurrences = (int)Math.Round(reputation.Support),
                    FirstSeen = reputation.FirstSeen,
                    LastSeen = reputation.LastSeen,
                    Action = reputation.State switch
                    {
                        ReputationState.ManuallyBlocked => LearnedPatternAction.Full,
                        ReputationState.ConfirmedBad => LearnedPatternAction.Full,
                        ReputationState.Suspect => LearnedPatternAction.ScoreOnly,
                        ReputationState.Neutral => LearnedPatternAction.LogOnly,
                        ReputationState.ConfirmedGood => LearnedPatternAction.LogOnly,
                        ReputationState.ManuallyAllowed => LearnedPatternAction.LogOnly,
                        _ => LearnedPatternAction.LogOnly
                    },
                    BotType = null,
                    BotName = null,
                    Source = "ReputationCache"
                };

                await _patternStore!.UpsertAsync(signature, ct);

                // Clear dirty flag after successful persistence
                if (_cache.TryGetValue(reputation.PatternId, out var entry)) entry.IsDirty = false;

                saved++;
            }

            if (saved > 0) _logger.LogDebug("Persisted {Count} pattern reputations to SQLite", saved);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist pattern reputation batch");
        }
    }

    /// <summary>
    ///     Get ephemeral-specific stats including hot key info.
    /// </summary>
    public EphemeralReputationStats GetEphemeralStats()
    {
        var patterns = _cache.Values.ToList();
        var now = DateTimeOffset.UtcNow;

        return new EphemeralReputationStats
        {
            BaseStats = GetStats(),
            HotKeyCount = patterns.Count(p => p.HotUntil > now),
            AverageAccessCount = patterns.Count > 0 ? patterns.Average(p => p.AccessCount) : 0,
            RecentSignalCount = _signals.Count,
            DecayQueueDepth = _decayCoordinator.PendingCount,
            ActiveDecayOperations = _decayCoordinator.ActiveCount
        };
    }

    private bool ShouldSample()
    {
        // Sample every 10th access to reduce signal noise
        var count = Interlocked.Increment(ref _accessCount);
        return count % 10 == 0;
    }

    // Signal constants
    public static class ReputationSignals
    {
        public const string PatternCreated = "reputation.created";
        public const string PatternUpdated = "reputation.updated";
        public const string StateChanged = "reputation.state_changed";
        public const string HotKey = "reputation.hot_key";
        public const string Evicted = "reputation.evicted";
        public const string DecaySweepStarted = "reputation.decay_sweep_started";
        public const string DecaySweepCompleted = "reputation.decay_sweep_completed";
        public const string GcStarted = "reputation.gc_started";
        public const string GcCompleted = "reputation.gc_completed";
    }

    // Internal cache entry with access tracking
    private sealed class CacheEntry
    {
        public CacheEntry(PatternReputation reputation, bool isDirty = true)
        {
            Reputation = reputation;
            LastAccess = DateTimeOffset.UtcNow;
            IsDirty = isDirty; // New entries need to be persisted, loaded entries don't
            HotUntil = DateTimeOffset.MinValue;
            AccessCount = 1;
        }

        public PatternReputation Reputation { get; set; }
        public DateTimeOffset LastAccess { get; set; }
        public DateTimeOffset HotUntil { get; set; }
        public int AccessCount { get; set; }
        public bool IsDirty { get; set; }
    }

    private readonly record struct DecayWork(DecayWorkType Type, int Count = 0);

    private enum DecayWorkType
    {
        DecaySweep,
        GarbageCollect,
        EvictCold
    }

    private readonly record struct PersistWork(IReadOnlyList<PatternReputation> Reputations);
}

/// <summary>
///     Extended stats including ephemeral-specific metrics.
/// </summary>
public class EphemeralReputationStats
{
    public required ReputationCacheStats BaseStats { get; init; }
    public int HotKeyCount { get; init; }
    public double AverageAccessCount { get; init; }
    public int RecentSignalCount { get; init; }
    public int DecayQueueDepth { get; init; }
    public int ActiveDecayOperations { get; init; }
}