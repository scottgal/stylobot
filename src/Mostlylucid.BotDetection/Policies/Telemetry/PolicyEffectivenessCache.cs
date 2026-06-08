using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Rules;

// Same ambiguity dance as SqlitePolicyDecisionLog: the legacy
// Mostlylucid.BotDetection.Policies.PolicyAction enum lives at the parent
// namespace of Telemetry and would shadow the new record hierarchy without an
// explicit alias.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.Policies.Telemetry;

/// <summary>
///     Write-behind LFU façade over <see cref="IPolicyDecisionLog"/>. The hot
///     path appends into a per-rule in-process ring buffer for sub-100us
///     aggregate reads; in parallel it enqueues the same decision into a
///     bounded channel drained to the durable log by a single background
///     drainer task. The ring is truth for the recent (largest tracked)
///     window; the durable log is truth for everything older and for cold
///     rules that have been LFU-evicted from the ring.
/// </summary>
/// <remarks>
///     Follows the same façade pattern as <c>SqlitePathLifecycleStore</c>:
///     ConcurrentDictionary + bounded channel + drainer, dict-is-truth for
///     recent, DB-is-durability for the long tail.
/// </remarks>
public sealed class PolicyEffectivenessCache : IPolicyEffectivenessCache, IAsyncDisposable
{
    private readonly IPolicyDecisionLog _durableLog;
    private readonly PolicyEffectivenessOptions _options;
    private readonly ILogger<PolicyEffectivenessCache> _logger;

    private readonly ConcurrentDictionary<Guid, RuleRing> _rings = new();
    private readonly ConcurrentDictionary<Guid, long> _accessCounter = new();
    private readonly Channel<PolicyDecision> _channel;
    private readonly object _evictionGate = new();

    private CancellationTokenSource? _drainerCts;
    private Task? _drainerTask;
    private long _droppedCount;
    private int _drainCycle;

    /// <summary>Count of decisions dropped because the channel was full.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public PolicyEffectivenessCache(
        IPolicyDecisionLog durableLog,
        IOptions<PolicyEffectivenessOptions> options,
        ILogger<PolicyEffectivenessCache> logger)
    {
        _durableLog = durableLog;
        _options = options.Value;
        _logger = logger;

        _channel = Channel.CreateBounded<PolicyDecision>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            // The durable log already has its own DropOldest channel; we mirror
            // the same behaviour here so a momentary stall doesn't unwind back
            // up the call chain to the request thread.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ValueTask OnDecisionAsync(PolicyDecision decision, CancellationToken ct = default)
    {
        // 1. Append to the per-rule ring -- this is what serves the hot read.
        var ring = _rings.GetOrAdd(decision.RuleId, _ => new RuleRing(_options.PerRuleRingCapacity));
        ring.Append(decision);

        // 2. Touch the access counter for LFU.
        TouchAccess(decision.RuleId);

        // 3. If we're over capacity, evict the coldest rule. Cheap O(N) scan
        //    over MaxRules (default 1024) is fine compared to the cost of the
        //    SQLite roundtrip we just avoided.
        if (_rings.Count > _options.MaxRules) EvictColdestRule(decision.RuleId);

        // 4. Try-write to the channel for the durable drainer. DropOldest means
        //    TryWrite only fails after writer-complete (disposal); count the
        //    drop so we can surface it later.
        if (!_channel.Writer.TryWrite(decision))
            Interlocked.Increment(ref _droppedCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<PolicyDecisionAggregate> GetAsync(
        Guid ruleId,
        TimeSpan window,
        CancellationToken ct = default)
    {
        if (_rings.TryGetValue(ruleId, out var ring))
        {
            TouchAccess(ruleId);
            var aggregate = ring.Aggregate(ruleId, window, DateTimeOffset.UtcNow);
            return new ValueTask<PolicyDecisionAggregate>(aggregate);
        }

        // Cold miss: fall through to the durable log. Spec says do NOT cache
        // the result -- the next OnDecisionAsync for this rule will reopen a
        // ring naturally.
        return new ValueTask<PolicyDecisionAggregate>(_durableLog.AggregateAsync(ruleId, window, ct));
    }

    public async ValueTask<IReadOnlyDictionary<Guid, PolicyDecisionAggregate>> GetManyAsync(
        IReadOnlyCollection<Guid> ruleIds,
        TimeSpan window,
        CancellationToken ct = default)
    {
        if (ruleIds.Count == 0)
            return new Dictionary<Guid, PolicyDecisionAggregate>();

        var result = new Dictionary<Guid, PolicyDecisionAggregate>(ruleIds.Count);
        var coldIds = new List<Guid>();
        var now = DateTimeOffset.UtcNow;

        foreach (var id in ruleIds)
        {
            if (_rings.TryGetValue(id, out var ring))
            {
                TouchAccess(id);
                result[id] = ring.Aggregate(id, window, now);
            }
            else
            {
                coldIds.Add(id);
            }
        }

        if (coldIds.Count > 0)
        {
            // Cold ids go to the durable log in parallel. SQLite serialises
            // under its own lock so true parallelism is limited, but Task.WhenAll
            // still wins over a serial await loop for an InMemory log and keeps
            // the API contract honest (single hop from the caller's POV).
            var coldTasks = coldIds.Select(id => _durableLog.AggregateAsync(id, window, ct)).ToArray();
            var coldAggregates = await Task.WhenAll(coldTasks).ConfigureAwait(false);
            for (var i = 0; i < coldIds.Count; i++)
                result[coldIds[i]] = coldAggregates[i];
        }

        return result;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_drainerTask is not null) return Task.CompletedTask;
        _drainerCts = new CancellationTokenSource();
        _drainerTask = Task.Run(() => DrainerLoopAsync(_drainerCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_drainerTask is null) return;

        _channel.Writer.TryComplete();
        try { _drainerCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already disposed */ }

        try { await _drainerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogDebug(ex, "PolicyEffectivenessCache drainer stop threw"); }

        _drainerTask = null;
        _drainerCts?.Dispose();
        _drainerCts = null;

        // Flush the durable log so the test (and shutdown) sees its writes land.
        try { await _durableLog.FlushAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "PolicyEffectivenessCache durable flush threw"); }
    }

    private async Task DrainerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_options.DrainerInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                await DrainOnceAsync(ct).ConfigureAwait(false);

                // Age the LFU counters once per drain cycle so long-lived hot
                // rules don't permanently dominate. We just halve every Nth
                // cycle -- cheap, conservative, no separate timer.
                var cycle = Interlocked.Increment(ref _drainCycle);
                if (cycle % 1000 == 0) AgeAccessCounters();
            }

            // Final drain on shutdown -- _channel.Writer was Completed in StopAsync.
            await DrainOnceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PolicyEffectivenessCache drainer terminated unexpectedly");
        }
    }

    private async Task DrainOnceAsync(CancellationToken ct)
    {
        var batchMax = _options.DrainerBatchSize;
        var drained = 0;

        while (drained < batchMax && _channel.Reader.TryRead(out var decision))
        {
            try
            {
                await _durableLog.AppendAsync(decision, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PolicyEffectivenessCache durable append failed; row dropped");
            }
            drained++;
        }
    }

    private void TouchAccess(Guid ruleId)
    {
        var cap = _options.LfuCounterCap;
        _accessCounter.AddOrUpdate(
            ruleId,
            addValue: 1L,
            updateValueFactory: (_, current) => current >= cap ? cap : current + 1L);
    }

    private void AgeAccessCounters()
    {
        // Halve every counter. Cheap O(N) over MaxRules. Done under no lock --
        // a concurrent TouchAccess racing with this is fine: we accept losing
        // a single increment in the rare overlap.
        foreach (var key in _accessCounter.Keys)
        {
            if (_accessCounter.TryGetValue(key, out var v))
                _accessCounter.TryUpdate(key, v / 2, v);
        }
    }

    private void EvictColdestRule(Guid excludingRuleId)
    {
        // Eviction is rare (only at capacity). Take a serialising lock so we
        // don't churn through multiple evictions on a thundering herd of new
        // rule ids.
        lock (_evictionGate)
        {
            if (_rings.Count <= _options.MaxRules) return;

            var coldestId = Guid.Empty;
            var coldestCount = long.MaxValue;
            foreach (var pair in _rings)
            {
                if (pair.Key == excludingRuleId) continue;
                var count = _accessCounter.TryGetValue(pair.Key, out var c) ? c : 0L;
                if (count < coldestCount)
                {
                    coldestCount = count;
                    coldestId = pair.Key;
                }
            }

            if (coldestId != Guid.Empty)
            {
                _rings.TryRemove(coldestId, out _);
                _accessCounter.TryRemove(coldestId, out _);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     Per-rule circular buffer of recent decisions. Sized at
    ///     <see cref="PolicyEffectivenessOptions.PerRuleRingCapacity"/>.
    ///     Append and snapshot operate under a single monitor lock so the
    ///     reader sees a consistent slice of slots.
    /// </summary>
    private sealed class RuleRing
    {
        private readonly object _gate = new();
        private readonly PolicyDecision[] _slots;
        private int _head;   // next write index
        private int _count;  // populated slot count

        public RuleRing(int capacity)
        {
            if (capacity <= 0) capacity = 1;
            _slots = new PolicyDecision[capacity];
        }

        public void Append(PolicyDecision d)
        {
            lock (_gate)
            {
                _slots[_head] = d;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length) _count++;
            }
        }

        /// <summary>
        ///     Compute the aggregate over rows newer than <c>now - window</c>.
        ///     We allocate exactly one List for latency samples and one
        ///     Dictionary for the win-distribution; no extra LINQ allocations
        ///     on the hot read path.
        /// </summary>
        public PolicyDecisionAggregate Aggregate(Guid ruleId, TimeSpan window, DateTimeOffset now)
        {
            var since = now - window;

            int matched = 0;
            int total = 0;
            var winDist = new Dictionary<string, int>(StringComparer.Ordinal);
            List<long>? samples = null;

            lock (_gate)
            {
                // Walk the populated slots. Slots are written in head-advancing
                // order; relative order within the snapshot doesn't matter for
                // any of the aggregate stats so we just visit every populated
                // slot directly.
                for (var i = 0; i < _count; i++)
                {
                    var slot = _slots[i];
                    if (slot is null) continue;
                    if (slot.ObservedAt < since) continue;

                    total++;
                    if (slot.Matched) matched++;

                    if (slot.WinnerRuleId == ruleId && slot.Matched)
                    {
                        var kind = SqlitePolicyDecisionLog.ActionKindName(slot.Action);
                        winDist[kind] = winDist.TryGetValue(kind, out var n) ? n + 1 : 1;
                    }

                    if (slot.EvalLatencyTicks > 0)
                    {
                        samples ??= new List<long>(_count);
                        samples.Add(slot.EvalLatencyTicks);
                    }
                }
            }

            long p50 = 0, p99 = 0;
            if (samples is { Count: > 0 })
            {
                samples.Sort();
                p50 = TicksToMicros(Percentile(samples, 0.50));
                p99 = TicksToMicros(Percentile(samples, 0.99));
            }

            return new PolicyDecisionAggregate(
                RuleId: ruleId,
                Window: window,
                Matched: matched,
                TotalEvaluations: total,
                WinDistribution: winDist,
                P50LatencyMicros: p50,
                P99LatencyMicros: p99,
                ComputedAt: now);
        }

        private static long Percentile(IReadOnlyList<long> sortedSamples, double p)
        {
            if (sortedSamples.Count == 0) return 0;
            if (sortedSamples.Count == 1) return sortedSamples[0];
            var idx = (int)Math.Ceiling(p * sortedSamples.Count) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sortedSamples.Count) idx = sortedSamples.Count - 1;
            return sortedSamples[idx];
        }

        private static long TicksToMicros(long ticks)
        {
            // Matches A6: EvalLatencyTicks is TimeSpan-ticks (100ns each), 10 ticks per microsecond.
            if (ticks <= 0) return 0;
            return ticks / 10L;
        }
    }
}
