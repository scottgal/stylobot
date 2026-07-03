using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Discrete write op enqueued for batched persistence. The drainer folds
///     a batch of these into SQLite UPSERTs in a single transaction; the merge
///     into the hot tier is handled by <see cref="SqlitePathLifecycleStore.MergeIntoExisting"/>.
///     <para>
///     <c>Domain</c> + <c>Host</c> carry the (domain, host) that owns the
///     observation so writes land under the right owner. Persistence uses
///     <c>(host, path)</c> as the natural key -- domain is the derived rollup
///     column.
///     </para>
/// </summary>
public sealed record PathLifecycleOp(string Domain, string Host, string Path, int StatusCode, DateTime UtcNow);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/>
///     for per-path response history. The hot tier (ConcurrentDictionary) is
///     authoritative for the live counters; the drainer flushes batched
///     UPSERTs into SQLite for cross-restart durability.
/// </summary>
/// <remarks>
///     <para>
///         The base class handles batching, LFU eviction, bounded channel
///         back-pressure (DropOldest), and the single background drainer.
///         <see cref="RecordResponseAsync"/> is a synchronous in-memory op
///         that returns in microseconds.
///     </para>
///     <para>
///         Adaptive behaviour comes from the base: under sustained write load
///         the drainer fills batches up to <c>batchMaxSize</c> per cycle (one
///         transaction per batch); under quiet periods partial batches flush
///         after <c>drainInterval</c>. The channel's <c>DropOldest</c> mode
///         provides automatic back-pressure -- if the durable tier can't keep
///         up, the oldest queued op is dropped (the hot tier still has the
///         latest aggregated state; only the persistence catch-up is lost).
///     </para>
///     <para>
///         Static asset paths (CSS/JS/images/fonts) are filtered at the
///         caller -- they would dominate the table without adding signal.
///     </para>
///     <para>
///         Multi-domain: the hot-tier key is <c>(host, path)</c> so the same
///         path served by different hosts is tracked independently. Domain
///         is persisted alongside for cross-host rollups. The <see cref="GetAsync"/>
///         cold path still keys by path only (single-host lookup) so short-lived
///         non-multi-domain deployments keep the O(path) read shape.
///     </para>
/// </remarks>
public sealed class SqlitePathLifecycleStore
    : WriteBehindLfuStore<(string Host, string Path), PathLifecycle, PathLifecycleOp>,
      IPathLifecycleStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqlitePathLifecycleStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqlitePathLifecycleStore(
        string connectionString,
        ILogger<SqlitePathLifecycleStore> logger,
        PathLifecycleOptions? opts = null)
        : base(
            maxEntries: (opts ?? new PathLifecycleOptions()).MaxEntries,
            writeQueueCapacity: (opts ?? new PathLifecycleOptions()).WriteQueueCapacity,
            batchMaxSize: (opts ?? new PathLifecycleOptions()).BatchMaxSize,
            drainInterval: (opts ?? new PathLifecycleOptions()).DrainInterval,
            logger: logger,
            keyComparer: null)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // === IPathLifecycleStore facade ========================================

    /// <summary>
    ///     Hot-path write: in-memory only. Returns immediately; persistence
    ///     happens out-of-band via the base-class drainer. <paramref name="ct"/>
    ///     is ignored -- the work is bounded local memory ops.
    /// </summary>
    public Task RecordResponseAsync(RequestScope scope, string path, int statusCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        Record((scope.Host, path), new PathLifecycleOp(scope.Domain, scope.Host, path, statusCode, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    Task<PathLifecycle?> IPathLifecycleStore.GetAsync(string path, CancellationToken ct)
        => GetAsync(path, ct);

    /// <summary>
    ///     Path-only read for the honeypot EndpointHistory classifier. Hot
    ///     tier is keyed by <c>(host, path)</c>; this scans the hot tier for
    ///     any host serving <paramref name="path"/>, then falls through to
    ///     Sqlite on miss. The cold row is not repopulated into the hot tier
    ///     from here (we don't know which host key the caller cares about);
    ///     the next <see cref="RecordResponseAsync"/> from middleware -- which
    ///     does know the host -- lands it back in the hot dict correctly.
    /// </summary>
    public async Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var hot = FirstHotByPath(path);
        return hot ?? await LoadFromDurableTierAsync(path, ct);
    }

    private PathLifecycle? FirstHotByPath(string path)
    {
        foreach (var (key, value) in Snapshot())
            if (string.Equals(key.Path, path, StringComparison.Ordinal))
                return value;
        return null;
    }

    /// <summary>
    ///     Test-only: drain any pending writes synchronously. Production
    ///     code never calls this -- the drainer handles persistence on its
    ///     own schedule.
    /// </summary>
    internal Task FlushAsync(CancellationToken ct = default) => FlushPendingAsync(ct);

    // === WriteBehindLfuStore hooks =========================================

    protected override PathLifecycle CreateInitial((string Host, string Path) key, PathLifecycleOp op)
    {
        var is2xx = op.StatusCode is >= 200 and < 300;
        var is4xx = op.StatusCode is >= 400 and < 500;
        return new PathLifecycle
        {
            Domain = op.Domain,
            Host = op.Host,
            Path = key.Path,
            FirstSeenUtc = op.UtcNow,
            LastSeenUtc = op.UtcNow,
            Total2xx = is2xx ? 1 : 0,
            Total4xx = is4xx ? 1 : 0,
            TotalOther = (!is2xx && !is4xx) ? 1 : 0,
            Last2xxUtc = is2xx ? op.UtcNow : null,
            First4xxAfter2xxUtc = null
        };
    }

    protected override PathLifecycle MergeIntoExisting((string Host, string Path) key, PathLifecycle prev, PathLifecycleOp op)
    {
        var is2xx = op.StatusCode is >= 200 and < 300;
        var is4xx = op.StatusCode is >= 400 and < 500;
        return prev with
        {
            // Refresh scope on merge so a null-seed row that later observes a
            // real scope picks it up. Same-host writes are a no-op update.
            Domain = op.Domain,
            Host = op.Host,
            LastSeenUtc = op.UtcNow,
            Total2xx = prev.Total2xx + (is2xx ? 1 : 0),
            Total4xx = prev.Total4xx + (is4xx ? 1 : 0),
            TotalOther = prev.TotalOther + ((!is2xx && !is4xx) ? 1 : 0),
            Last2xxUtc = is2xx ? op.UtcNow : prev.Last2xxUtc,
            // Lock in the first 4xx that lands AFTER any 2xx history.
            First4xxAfter2xxUtc =
                is4xx && prev.Total2xx > 0 && !prev.First4xxAfter2xxUtc.HasValue
                    ? op.UtcNow
                    : prev.First4xxAfter2xxUtc
        };
    }

    /// <summary>
    ///     Behaviourally shaped coldness. Higher score = warmer = kept in
    ///     the hot tier longer. The base class evicts lowest-score first.
    ///     <para>
    ///     This score also drives sampling under overload: when the write
    ///     queue is saturated and entries get evicted from the hot tier,
    ///     <see cref="PersistBatchAsync"/> skips any op whose (host,path) is
    ///     no longer in the hot tier (TryGetHot returns null). So the LFU
    ///     ranking here decides both what stays in memory AND what gets
    ///     persisted under pressure -- the system sheds the least useful
    ///     paths first, not the oldest.
    ///     </para>
    ///     <para>
    ///     Tiers (high to low priority for retention):
    ///     <list type="number">
    ///       <item>FormerlyReal (2xx history + 4xx after): THE signal for
    ///         EndpointHistoryContributor. Pinned -- never sampled out.</item>
    ///       <item>Mixed (2xx history + ongoing 4xx scanner activity).</item>
    ///       <item>HadRealTraffic (2xx history only -- useful for future flip).</item>
    ///       <item>404-spam (no 2xx history): cheap to lose, evict first.</item>
    ///     </list>
    ///     Last-seen ticks are the within-tier tiebreaker so active scanners
    ///     stay resident over long-idle history within the same tier.
    ///     </para>
    /// </summary>
    protected override long ColdnessScore(PathLifecycle entry)
    {
        // Bit-shifted tiers so the within-tier last-seen tiebreaker can never
        // promote a lower-tier entry above a higher one.
        const long PinFormerlyReal = 1L << 60;
        const long TierMixed       = 1L << 55;
        const long TierHadReal     = 1L << 50;

        long score = entry.LastSeenUtc.Ticks;
        if (entry.IsFormerlyReal)                                          score += PinFormerlyReal;
        else if (entry.HasEverServed2xx && entry.Total4xx > 0)             score += TierMixed;
        else if (entry.HasEverServed2xx)                                   score += TierHadReal;
        // else: 4xx-only path, no bonus -- coldest tier, sampled out first.
        return score;
    }

    protected override async ValueTask<PathLifecycle?> LoadFromDurableTierAsync((string Host, string Path) key, CancellationToken ct)
    {
        return await LoadFromDurableTierAsync(key.Path, ct);
    }

    private async ValueTask<PathLifecycle?> LoadFromDurableTierAsync(string path, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT domain, host, first_seen_utc, total_2xx, total_4xx, total_other,
                       last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc
                FROM path_lifecycle WHERE path = @path
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@path", path);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new PathLifecycle
            {
                Domain = reader.GetString(0),
                Host = reader.GetString(1),
                Path = path,
                FirstSeenUtc = ParseUtc(reader.GetString(2)),
                Total2xx = reader.GetInt32(3),
                Total4xx = reader.GetInt32(4),
                TotalOther = reader.GetInt32(5),
                Last2xxUtc = reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
                First4xxAfter2xxUtc = reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
                LastSeenUtc = ParseUtc(reader.GetString(8))
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PathLifecycle cold load failed for {Path}", path);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<PathLifecycleOp> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await EnsureInitializedAsync(ct);

        // Dedupe by (host, path): many ops in a single batch can land on the
        // same (host, path) under load. We only need to UPSERT the current
        // aggregated value once per unique key -- the hot tier already holds
        // the merged result.
        var keys = new HashSet<(string Host, string Path)>(batch.Count);
        foreach (var op in batch) keys.Add((op.Host, op.Path));

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO path_lifecycle
                    (domain, host, path, first_seen_utc, total_2xx, total_4xx, total_other,
                     last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc)
                VALUES
                    (@domain, @host, @path, @first_seen, @t2, @t4, @to, @last2, @first4, @last_seen)
                ON CONFLICT(host, path) DO UPDATE SET
                    domain = excluded.domain,
                    total_2xx = excluded.total_2xx,
                    total_4xx = excluded.total_4xx,
                    total_other = excluded.total_other,
                    last_2xx_utc = excluded.last_2xx_utc,
                    first_4xx_after_2xx_utc = COALESCE(path_lifecycle.first_4xx_after_2xx_utc, excluded.first_4xx_after_2xx_utc),
                    last_seen_utc = excluded.last_seen_utc
                """;
            var pDomain = cmd.Parameters.Add("@domain", SqliteType.Text);
            var pHost = cmd.Parameters.Add("@host", SqliteType.Text);
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pFirst = cmd.Parameters.Add("@first_seen", SqliteType.Text);
            var pT2 = cmd.Parameters.Add("@t2", SqliteType.Integer);
            var pT4 = cmd.Parameters.Add("@t4", SqliteType.Integer);
            var pTo = cmd.Parameters.Add("@to", SqliteType.Integer);
            var pLast2 = cmd.Parameters.Add("@last2", SqliteType.Text);
            var pFirst4 = cmd.Parameters.Add("@first4", SqliteType.Text);
            var pLastSeen = cmd.Parameters.Add("@last_seen", SqliteType.Text);

            foreach (var key in keys)
            {
                var entry = TryGetHot(key);
                if (entry is null) continue;
                pDomain.Value = entry.Domain;
                pHost.Value = entry.Host;
                pPath.Value = entry.Path;
                pFirst.Value = entry.FirstSeenUtc.ToString("O");
                pT2.Value = entry.Total2xx;
                pT4.Value = entry.Total4xx;
                pTo.Value = entry.TotalOther;
                pLast2.Value = (object?)entry.Last2xxUtc?.ToString("O") ?? DBNull.Value;
                pFirst4.Value = (object?)entry.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value;
                pLastSeen.Value = entry.LastSeenUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // === Init + helpers ====================================================

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Mostlylucid.BotDetection.Data.Schema.SchemaLoader.Load("path_lifecycle");
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Test-only synchronous drain. The base class doesn't expose its
    ///     drainer; we replicate the persist path here by snapshotting the
    ///     hot tier and writing one UPSERT for each entry. Production uses
    ///     the channel-driven drainer.
    /// </summary>
    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var snapshot = Snapshot().ToArray();
        if (snapshot.Length == 0) return;
        await EnsureInitializedAsync(ct);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO path_lifecycle
                    (domain, host, path, first_seen_utc, total_2xx, total_4xx, total_other,
                     last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc)
                VALUES
                    (@domain, @host, @path, @first_seen, @t2, @t4, @to, @last2, @first4, @last_seen)
                ON CONFLICT(host, path) DO UPDATE SET
                    domain = excluded.domain,
                    total_2xx = excluded.total_2xx,
                    total_4xx = excluded.total_4xx,
                    total_other = excluded.total_other,
                    last_2xx_utc = excluded.last_2xx_utc,
                    first_4xx_after_2xx_utc = COALESCE(path_lifecycle.first_4xx_after_2xx_utc, excluded.first_4xx_after_2xx_utc),
                    last_seen_utc = excluded.last_seen_utc
                """;
            foreach (var (_, entry) in snapshot)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@domain", entry.Domain);
                cmd.Parameters.AddWithValue("@host", entry.Host);
                cmd.Parameters.AddWithValue("@path", entry.Path);
                cmd.Parameters.AddWithValue("@first_seen", entry.FirstSeenUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@t2", entry.Total2xx);
                cmd.Parameters.AddWithValue("@t4", entry.Total4xx);
                cmd.Parameters.AddWithValue("@to", entry.TotalOther);
                cmd.Parameters.AddWithValue("@last2", (object?)entry.Last2xxUtc?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@first4", (object?)entry.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@last_seen", entry.LastSeenUtc.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}

/// <summary>
///     Tuning knobs for <see cref="SqlitePathLifecycleStore"/>. Exposed via
///     <c>BotDetectionOptions.PathLifecycle</c> so users bind through the
///     normal <c>BotDetection:PathLifecycle:*</c> section.
/// </summary>
public sealed class PathLifecycleOptions
{
    /// <summary>Hot-tier LFU cap. Once exceeded by ~10%, coldest entries
    ///     (by last-seen) are evicted. Default: 50,000.</summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>Bounded channel capacity. When full, the oldest queued op
    ///     is dropped to keep Record() non-blocking. Default: 100,000.</summary>
    public int WriteQueueCapacity { get; set; } = 100_000;

    /// <summary>Max ops folded into one SQLite transaction. Larger = fewer
    ///     round-trips, higher per-batch latency. Default: 256.</summary>
    public int BatchMaxSize { get; set; } = 256;

    /// <summary>Sleep between batches. Smaller = lower persistence latency,
    ///     more I/O. Default: 250ms.</summary>
    public TimeSpan DrainInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}