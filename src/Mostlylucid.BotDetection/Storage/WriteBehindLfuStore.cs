using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Storage;

/// <summary>
///     Reusable write-behind, read-through, LFU-bounded façade. The universal
///     in-memory-store pattern across the whole codebase: hot
///     <see cref="ConcurrentDictionary{TKey,TValue}"/> tier + bounded
///     <see cref="Channel{T}"/> drained by a single background writer task that
///     batches inserts to the durable tier.
///     <para>
///     The dict is the SOURCE OF TRUTH for recent state. The durable tier
///     (Postgres / SQLite) is durability + cold restore. They cannot diverge:
///     writes update the dict synchronously and then enqueue for batched
///     persistence; reads come from the dict and only fall through to the
///     durable tier on cold miss (and then populate the dict).
///     </para>
///     <para>
///     NO SYNCHRONOUS DB I/O ON THE HOT PATH. SQLite cannot take per-detection
///     writes; Postgres at scale won't either. <see cref="Record"/> returns in
///     microseconds. The drainer batches into the durable tier on its own
///     schedule.
///     </para>
///     <para>
///     Subclasses provide the merge (folding an incoming write into an existing
///     dict entry), the cold load (read-through from durable tier on miss),
///     and the batch persist (write a batch of mutations to the durable tier).
///     Eviction is LFU-style, configurable threshold + batch size.
///     </para>
/// </summary>
/// <typeparam name="TKey">Identity key for the entry (e.g. primary signature).</typeparam>
/// <typeparam name="TValue">Aggregate value held in memory.</typeparam>
/// <typeparam name="TWriteOp">Discrete mutation enqueued for the drainer
///     (often the same as <typeparamref name="TValue"/>; can be a delta).</typeparam>
public abstract class WriteBehindLfuStore<TKey, TValue, TWriteOp> : IDisposable
    where TKey : notnull
    where TValue : class
{
    private readonly ConcurrentDictionary<TKey, TValue> _entries;
    // Behavioural-sample drain (Gap A): keys mutated since the last flush. The
    // drainer pulls the CURRENT value for these, coalesced by key (a hot key
    // that mutated 1000× this cycle appears once), ordered by significance, and
    // persists each once. Empty for op-replay subclasses (UseBehaviouralSampleDrain=false).
    private readonly ConcurrentDictionary<TKey, byte> _dirtyKeys;
    private readonly Channel<TWriteOp> _writeQueue;
    private readonly Task _drainer;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ILogger _logger;
    private readonly int _maxEntries;
    // Optional memory-pressure governor. When set, the drainer refreshes
    // _effectiveMax from it each cycle so the hot-path Record stays a cheap
    // volatile read rather than a per-write GC probe. Null → fixed _maxEntries.
    private readonly Func<int>? _maxEntriesProvider;
    private volatile int _effectiveMax;
    private readonly int _batchMaxSize;
    private readonly TimeSpan _drainInterval;
    private long _writes;

    /// <param name="maxEntries">LFU eviction threshold for the hot tier. Once
    ///     the dict exceeds this by ~10% the coldest entries are evicted in
    ///     batch.</param>
    /// <param name="writeQueueCapacity">Bounded channel capacity. When full,
    ///     the oldest queued write is dropped so <see cref="Record"/> stays
    ///     non-blocking. The dict still has the latest value; only the
    ///     persistence delta is lost.</param>
    /// <param name="batchMaxSize">Max items in one durable-tier batch flush.</param>
    /// <param name="drainInterval">Maximum wait before flushing a partial
    ///     batch. Smaller = lower persistence latency, more DB round-trips.</param>
    /// <param name="logger">Logger used to surface batch flush failures and shed events.</param>
    /// <param name="keyComparer">Optional comparer for the hot-tier dictionary keys.</param>
    /// <param name="maxEntriesProvider">Optional adaptive cap. When supplied, the
    ///     drainer refreshes the effective eviction threshold from it each cycle
    ///     (e.g. a <see cref="MemoryAdaptiveCap"/> that shrinks under memory
    ///     pressure). Null keeps the fixed <paramref name="maxEntries"/>.</param>
    protected WriteBehindLfuStore(
        int maxEntries,
        int writeQueueCapacity,
        int batchMaxSize,
        TimeSpan drainInterval,
        ILogger logger,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<int>? maxEntriesProvider = null)
    {
        _maxEntries = maxEntries;
        _maxEntriesProvider = maxEntriesProvider;
        _effectiveMax = maxEntries;
        _batchMaxSize = batchMaxSize;
        _drainInterval = drainInterval;
        _logger = logger;
        _entries = keyComparer is null
            ? new ConcurrentDictionary<TKey, TValue>()
            : new ConcurrentDictionary<TKey, TValue>(keyComparer);
        _dirtyKeys = keyComparer is null
            ? new ConcurrentDictionary<TKey, byte>()
            : new ConcurrentDictionary<TKey, byte>(keyComparer);
        _writeQueue = Channel.CreateBounded<TWriteOp>(new BoundedChannelOptions(writeQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _drainer = Task.Run(DrainLoopAsync);
    }

    /// <summary>Current number of in-memory entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Read-through fetch. Hot path = dict; cold miss falls through to
    ///     <see cref="LoadFromDurableTierAsync"/> and populates the dict.
    /// </summary>
    public async ValueTask<TValue?> GetAsync(TKey key, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(key, out var hot)) return hot;
        TValue? cold;
        try { cold = await LoadFromDurableTierAsync(key, ct); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Store} cold load failed for {Key}", GetType().Name, key);
            return null;
        }
        if (cold is null) return null;
        // Cache-aside populate: if someone else won the race they already
        // have a fresh value; keep it. Otherwise install ours.
        _entries.TryAdd(key, cold);
        return _entries.TryGetValue(key, out var current) ? current : cold;
    }

    /// <summary>Synchronous hot-path read. Returns null on cold miss
    ///     (caller should fall back to <see cref="GetAsync"/>).</summary>
    public TValue? TryGetHot(TKey key) =>
        _entries.TryGetValue(key, out var v) ? v : null;

    /// <summary>Snapshot every in-memory entry. O(n); use sparingly.</summary>
    protected IEnumerable<KeyValuePair<TKey, TValue>> Snapshot() => _entries.ToArray();

    /// <summary>
    ///     Synchronous write. Folds <paramref name="op"/> into the dict (via
    ///     <see cref="MergeIntoExisting"/> / <see cref="CreateInitial"/>) and
    ///     enqueues for batched persistence. Returns the merged value.
    ///     Never blocks on durable-tier I/O.
    /// </summary>
    public TValue Record(TKey key, TWriteOp op)
    {
        var merged = _entries.AddOrUpdate(
            key,
            _ => CreateInitial(key, op),
            (_, prev) => MergeIntoExisting(key, prev, op));

        // Bounded LFU eviction. EvictColdest is O(n) so amortise by only
        // firing when 10% over the (possibly memory-adapted) effective cap.
        if (_entries.Count > _effectiveMax + _effectiveMax / 10)
            EvictColdest();

        if (UseBehaviouralSampleDrain)
        {
            // Behavioural sample: just flag the key dirty. The drainer pulls the
            // coalesced current value once per cycle. A hot key that mutates N
            // times this cycle persists once, not N times.
            _dirtyKeys[key] = 0;
        }
        else
        {
            // Op-replay path. Non-blocking enqueue; DropOldest discards the
            // oldest if full, not the newest -- we'd rather lose stale
            // persistence catch-up than recent state.
            _writeQueue.Writer.TryWrite(op);
        }
        Interlocked.Increment(ref _writes);
        return merged;
    }

    /// <summary>
    ///     Opt in to the behavioural-sample drain (Gap A): the drainer flushes the
    ///     highest-significance dirty entries' CURRENT values, coalesced by key,
    ///     once per cycle, instead of replaying every enqueued write op. Bounds
    ///     write VOLUME, not just row count. Subclasses that opt in must override
    ///     <see cref="PersistValuesBatchAsync"/> (a keyed upsert) instead of
    ///     <see cref="PersistBatchAsync"/>. Default false keeps the op-replay path.
    /// </summary>
    protected virtual bool UseBehaviouralSampleDrain => false;

    /// <summary>
    ///     Persist a coalesced batch of CURRENT values to the durable tier, one
    ///     keyed upsert per entry. Called by the behavioural-sample drainer when
    ///     <see cref="UseBehaviouralSampleDrain"/> is true. Failures are logged and
    ///     the keys re-marked dirty for the next cycle. Default no-op so op-replay
    ///     subclasses need not implement it.
    /// </summary>
    protected virtual Task PersistValuesBatchAsync(
        IReadOnlyList<KeyValuePair<TKey, TValue>> batch, CancellationToken ct) => Task.CompletedTask;

    /// <summary>How to build a brand-new entry from a write op (key not in
    ///     dict yet). Subclasses fold per-record fields (initial hit count,
    ///     timestamps, copied values).</summary>
    protected abstract TValue CreateInitial(TKey key, TWriteOp op);

    /// <summary>How to fold a new write op into an existing entry (hit count
    ///     increment, timestamp update, etc.). Must be idempotent-safe under
    ///     concurrent updates (ConcurrentDictionary.AddOrUpdate may retry).</summary>
    protected abstract TValue MergeIntoExisting(TKey key, TValue existing, TWriteOp op);

    /// <summary>Read-through cold load. Returns the persisted aggregate for
    ///     <paramref name="key"/> or null if the durable tier has no row.
    ///     Called by <see cref="GetAsync"/> on miss.</summary>
    protected abstract ValueTask<TValue?> LoadFromDurableTierAsync(TKey key, CancellationToken ct);

    /// <summary>Persist a batch of write ops to the durable tier. The drainer
    ///     calls this with up to <c>batchMaxSize</c> ops in one go. Failures
    ///     are logged and swallowed -- the dict remains authoritative for
    ///     in-memory state; the next batch will retry the lost ops via the
    ///     next write that lands on those keys.</summary>
    protected abstract Task PersistBatchAsync(IReadOnlyList<TWriteOp> batch, CancellationToken ct);

    /// <summary>How LFU eviction ranks entries (lower score = colder = evicted
    ///     first). Default: 0 (i.e. no preference -- subclasses override).
    ///     Typical implementations return last-seen ticks or hit count.</summary>
    protected virtual long ColdnessScore(TValue entry) => 0;

    private void EvictColdest()
    {
        var target = _effectiveMax - _effectiveMax / 10;
        var overflow = _entries.Count - target;
        if (overflow <= 0) return;

        // Build a min-heap-equivalent via OrderBy. O(n log n) on the hot
        // path is acceptable because the trigger threshold is 10% over and
        // the batch removes ~10% at a time.
        var coldest = _entries
            .OrderBy(kv => ColdnessScore(kv.Value))
            .Take(overflow)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in coldest) _entries.TryRemove(k, out _);
    }

    private Task DrainLoopAsync() =>
        UseBehaviouralSampleDrain ? SampleDrainLoopAsync() : OpReplayDrainLoopAsync();

    /// <summary>
    ///     Behavioural-sample drainer (Gap A). Each cycle: pull the dirty keys,
    ///     read their CURRENT values, order by significance (<see cref="ColdnessScore"/>
    ///     descending), claim + persist the top <c>batchMaxSize</c> as one keyed-upsert
    ///     batch, and leave the rest dirty for the next cycle. A hot shape that mutated
    ///     many times persists once; an unchanged shape doesn't persist at all.
    /// </summary>
    private async Task SampleDrainLoopAsync()
    {
        var ct = _shutdownCts.Token;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_drainInterval, ct); }
            catch (OperationCanceledException) { break; }

            if (_maxEntriesProvider is not null)
            {
                _effectiveMax = Math.Max(1, _maxEntriesProvider());
                EvictColdest();
            }

            try { await DrainDirtyOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    /// <summary>
    ///     One sample-drain pass: gather the dirty keys' CURRENT values, order by
    ///     significance (<see cref="ColdnessScore"/> descending), claim + persist the top
    ///     <c>batchMaxSize</c> as one keyed-upsert batch, and leave the rest dirty. Returns
    ///     the number of entries persisted (0 = nothing dirty, all evicted, or persist failed).
    /// </summary>
    private async Task<int> DrainDirtyOnceAsync(CancellationToken ct, bool throwOnPersistError = false)
    {
        if (_dirtyKeys.IsEmpty) return 0;

        // Phase 1: gather current values for the dirty keys, drop any evicted since they
        // were flagged, and order by significance (highest first).
        var candidates = new List<KeyValuePair<TKey, TValue>>(_dirtyKeys.Count);
        foreach (var k in _dirtyKeys.Keys)
        {
            if (_entries.TryGetValue(k, out var v)) candidates.Add(new(k, v));
            else _dirtyKeys.TryRemove(k, out _); // evicted before flush: nothing to persist
        }
        if (candidates.Count == 0) return 0;
        candidates.Sort((a, b) => ColdnessScore(b.Value).CompareTo(ColdnessScore(a.Value)));

        // Phase 2: claim (remove dirty flag) + re-read current value + persist the top
        // batch. Claiming before the re-read means a mutation that lands after we claim
        // re-flags the key, so the next cycle re-persists the newer value; we never lose an
        // update, at worst persist one redundantly.
        var batch = new List<KeyValuePair<TKey, TValue>>(Math.Min(_batchMaxSize, candidates.Count));
        for (var i = 0; i < candidates.Count && batch.Count < _batchMaxSize; i++)
        {
            var k = candidates[i].Key;
            if (!_dirtyKeys.TryRemove(k, out _)) continue;
            if (_entries.TryGetValue(k, out var cur)) batch.Add(new(k, cur));
        }
        if (batch.Count == 0) return 0;

        try
        {
            await PersistValuesBatchAsync(batch, ct);
            return batch.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Re-flag so a later cycle (or the caller) can retry; the dict stays authoritative.
            foreach (var kv in batch) _dirtyKeys[kv.Key] = 0;
            _logger.LogWarning(ex,
                "{Store} sample-drainer: batch of {Count} re-queued after persist error",
                GetType().Name, batch.Count);
            // The background drainer fails open (retry next cycle). A caller that needs a
            // durability barrier (FlushDirtyAsync) passes throwOnPersistError so the failure
            // surfaces instead of masquerading as "nothing left to flush".
            if (throwOnPersistError) throw;
            return 0;
        }
    }

    /// <summary>
    ///     Synchronously persist ALL currently-dirty entries now, via the same
    ///     significance-ordered batches the background drainer uses, until nothing is dirty.
    ///     Removes the dependency on the timer-driven drain, so tests get deterministic
    ///     persistence instead of racing the drain interval.
    ///     <para>
    ///     Unlike the background drainer (which fails open and retries next cycle), this is a
    ///     durability barrier: a persist failure THROWS rather than returning silently, so a
    ///     caller can assert the flush actually reached the durable tier. It returns normally
    ///     only when every dirty entry was persisted (or evicted before it could be flushed).
    ///     </para>
    ///     No-op on the op-replay path. This does NOT run on shutdown: <see cref="Dispose"/>
    ///     cancels the drainer without flushing, so dirty-but-undrained samples are dropped by
    ///     design (the write is a bounded SAMPLE, not a log). internal: tests in
    ///     Mostlylucid.BotDetection.Test.
    /// </summary>
    internal async Task FlushDirtyAsync(CancellationToken ct = default)
    {
        if (!UseBehaviouralSampleDrain) return;
        while (!_dirtyKeys.IsEmpty && !ct.IsCancellationRequested)
        {
            // Stop only when a pass persists nothing LEGITIMATELY (all remaining keys were
            // evicted before we could flush them). A persist FAILURE throws instead of
            // returning 0, so a dead durable tier surfaces as an exception, never a false
            // "flushed" result.
            if (await DrainDirtyOnceAsync(ct, throwOnPersistError: true) == 0) break;
        }
    }

    private async Task OpReplayDrainLoopAsync()
    {
        var ct = _shutdownCts.Token;
        var batch = new List<TWriteOp>(_batchMaxSize);
        var reader = _writeQueue.Reader;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one op (or shutdown). When shutdown fires
                // the channel completes and the next ReadAsync throws --
                // caught below, loop exits.
                if (!await reader.WaitToReadAsync(ct)) break;
            }
            catch (OperationCanceledException) { break; }

            // Refresh the effective cap from the adaptive provider (if any) on
            // the drainer's cadence, then shed promptly if memory pressure just
            // shrank it — the hot-path Record only reads the cached volatile.
            if (_maxEntriesProvider is not null)
            {
                _effectiveMax = Math.Max(1, _maxEntriesProvider());
                EvictColdest();
            }

            batch.Clear();
            // Drain everything queued up to the batch cap.
            while (batch.Count < _batchMaxSize && reader.TryRead(out var op))
                batch.Add(op);

            if (batch.Count == 0) continue;

            try
            {
                await PersistBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Store} drainer: batch of {Count} dropped on persist error",
                    GetType().Name, batch.Count);
            }

            // Brief pause between batches so we don't hammer the durable
            // tier under sustained write load. Drainer wakes early if the
            // channel has data; this only adds latency to small partial
            // batches.
            try { await Task.Delay(_drainInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Override to run a startup catch-up rehydrate -- typically a
    ///     bounded SELECT to seed the dict with the hottest recent entries
    ///     from the durable tier. Called once by the host on first use; the
    ///     base class does not call this itself because the timing depends
    ///     on host bootstrapping order (DB connection pool, schema init).</summary>
    public virtual Task WarmFromDurableTierAsync(CancellationToken ct = default) => Task.CompletedTask;

    private int _disposed;

    public void Dispose()
    {
        // Idempotent: DI containers can dispose a singleton twice when scope and
        // root-provider teardown overlap. The second Cancel() would throw
        // ObjectDisposedException because the first call already disposed the CTS.
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _writeQueue.Writer.TryComplete(); } catch { }
        try { _shutdownCts.Cancel(); } catch (ObjectDisposedException) { }
        try { _drainer.Wait(TimeSpan.FromSeconds(5)); } catch { }
        _shutdownCts.Dispose();
    }
}