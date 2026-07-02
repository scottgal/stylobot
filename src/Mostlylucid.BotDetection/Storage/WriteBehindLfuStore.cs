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

        // Non-blocking enqueue. DropOldest mode discards the oldest if full,
        // not the newest -- we'd rather lose stale persistence catch-up than
        // recent state.
        _writeQueue.Writer.TryWrite(op);
        Interlocked.Increment(ref _writes);
        return merged;
    }

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

    private async Task DrainLoopAsync()
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