using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Discrete response-observation op enqueued for batched persistence. Mirrors
///     <see cref="ResponseSignal"/>'s fields the aggregate actually needs -- status-code
///     bucket, 404 path (for uniqueness), honeypot-hit flag.
/// </summary>
public sealed record ResponseHistoryOp(
    string ClientId, int StatusCode, string? Path, bool IsHoneypotHit, DateTimeOffset UtcNow);

/// <summary>
///     Cross-restart-durable aggregate for one client's response history. Immutable
///     record -- <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}.Record"/> folds ops via
///     <c>ConcurrentDictionary.AddOrUpdate</c>, whose update factory can run more than once
///     under contention, so the merge must be a pure function of (existing, op), never an
///     in-place mutation (see <see cref="SqlitePathLifecycleStore"/> for the same pattern).
/// </summary>
/// <remarks>
///     <see cref="NotFoundPaths"/> is the LIVE dedup set, tracked only while this entry is
///     resident in the hot tier -- it is NOT persisted to SQLite (see
///     <see cref="SqliteResponseHistoryStore.PersistBatchAsync"/>); only its count is. A cold
///     load from the durable tier seeds <see cref="SeededUniqueNotFoundPathsBaseline"/> instead
///     of reconstructing the exact set (the set was never persisted), so post-restart
///     uniqueness is additive from that baseline, not exact against pre-restart paths --
///     deliberate: the alternative is an unbounded per-client path list in SQLite, and every
///     detection consumer (<see cref="Atoms.ResponseBehaviorAtom"/>) only ever reads the count.
/// </remarks>
public sealed record ResponseHistoryAggregate
{
    public required string ClientId { get; init; }
    public required DateTimeOffset FirstSeenUtc { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }
    public required int TotalCount { get; init; }
    public required int Count2xx { get; init; }
    public required int Count3xx { get; init; }
    public required int Count4xx { get; init; }
    public required int Count5xx { get; init; }
    public required int Count404 { get; init; }
    public required int AuthFailures { get; init; }
    public required int HoneypotHits { get; init; }
    public ImmutableHashSet<string> NotFoundPaths { get; init; } = ImmutableHashSet<string>.Empty;
    public int SeededUniqueNotFoundPathsBaseline { get; init; }
    public int UniqueNotFoundPathCount => NotFoundPaths.Count + SeededUniqueNotFoundPathsBaseline;
}

/// <summary>
///     Tuning knobs for <see cref="SqliteResponseHistoryStore"/>. Bound via
///     <c>BotDetection:ResponseHistoryPersistence:*</c>. Mirrors <see cref="Lifecycle.PathLifecycleOptions"/>'s
///     shape -- same write-behind-LFU tuning surface every store in this family exposes.
/// </summary>
public sealed class ResponseHistoryPersistenceOptions
{
    /// <summary>Hot-tier LFU cap. Default: 20,000 (matches
    ///     <see cref="ResponseCoordinatorOptions.MaxClientsInWindow"/>'s default x4 --
    ///     durable-tier retention outlives the in-memory sliding-cache window on purpose).</summary>
    public int MaxEntries { get; set; } = 20_000;

    /// <summary>Bounded channel capacity. Default: 50,000.</summary>
    public int WriteQueueCapacity { get; set; } = 50_000;

    /// <summary>Max ops folded into one SQLite transaction. Default: 256.</summary>
    public int BatchMaxSize { get; set; } = 256;

    /// <summary>Sleep between batches. Default: 250ms.</summary>
    public TimeSpan DrainInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> for
///     <see cref="ResponseCoordinator"/>'s per-client response history -- the durable tier
///     satisfying the CLAUDE.md rule ("NEVER use in-memory stores for persistence... for
///     anything that matters") that <see cref="ClientResponseTrackingAtom"/>'s bounded,
///     TTL-evicted, in-process-only history violated once it was wired into DI and the live
///     request path.
/// </summary>
/// <remarks>
///     Same file/directory convention as <see cref="Identity.SqliteFingerprintStore"/> /
///     <see cref="Lifecycle.SqlitePathLifecycleStore"/>: own sibling db file derived from
///     <see cref="Models.BotDetectionOptions.DatabasePath"/>'s directory, not a shared
///     connection string threaded through DI.
/// </remarks>
public sealed class SqliteResponseHistoryStore
    : WriteBehindLfuStore<string, ResponseHistoryAggregate, ResponseHistoryOp>
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteResponseHistoryStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteResponseHistoryStore(
        ILogger<SqliteResponseHistoryStore> logger,
        IOptions<Models.BotDetectionOptions> options,
        IOptions<ResponseHistoryPersistenceOptions>? persistenceOptions = null)
        : this(
            logger,
            ResolveConnectionString(options.Value),
            persistenceOptions?.Value ?? new ResponseHistoryPersistenceOptions())
    {
    }

    internal SqliteResponseHistoryStore(
        ILogger<SqliteResponseHistoryStore> logger,
        string connectionString,
        ResponseHistoryPersistenceOptions? opts = null)
        : base(
            maxEntries: (opts ?? new ResponseHistoryPersistenceOptions()).MaxEntries,
            writeQueueCapacity: (opts ?? new ResponseHistoryPersistenceOptions()).WriteQueueCapacity,
            batchMaxSize: (opts ?? new ResponseHistoryPersistenceOptions()).BatchMaxSize,
            drainInterval: (opts ?? new ResponseHistoryPersistenceOptions()).DrainInterval,
            logger: logger)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    private static string ResolveConnectionString(Models.BotDetectionOptions options)
    {
        var dbPath = options.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
        var dataDir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        return $"Data Source={Path.Combine(dataDir, "response-history.db")};Cache=Shared;Pooling=true";
    }

    // === WriteBehindLfuStore hooks ==========================================

    protected override ResponseHistoryAggregate CreateInitial(string key, ResponseHistoryOp op)
    {
        var (is2xx, is3xx, is4xx, is5xx, is404) = Classify(op.StatusCode);
        return new ResponseHistoryAggregate
        {
            ClientId = key,
            FirstSeenUtc = op.UtcNow,
            LastSeenUtc = op.UtcNow,
            TotalCount = 1,
            Count2xx = is2xx ? 1 : 0,
            Count3xx = is3xx ? 1 : 0,
            Count4xx = is4xx ? 1 : 0,
            Count5xx = is5xx ? 1 : 0,
            Count404 = is404 ? 1 : 0,
            AuthFailures = op.StatusCode is 401 or 403 ? 1 : 0,
            HoneypotHits = op.IsHoneypotHit ? 1 : 0,
            NotFoundPaths = is404 && op.Path is { Length: > 0 }
                ? ImmutableHashSet.Create(StringComparer.Ordinal, op.Path)
                : ImmutableHashSet<string>.Empty
        };
    }

    protected override ResponseHistoryAggregate MergeIntoExisting(
        string key, ResponseHistoryAggregate existing, ResponseHistoryOp op)
    {
        var (is2xx, is3xx, is4xx, is5xx, is404) = Classify(op.StatusCode);
        return existing with
        {
            LastSeenUtc = op.UtcNow,
            TotalCount = existing.TotalCount + 1,
            Count2xx = existing.Count2xx + (is2xx ? 1 : 0),
            Count3xx = existing.Count3xx + (is3xx ? 1 : 0),
            Count4xx = existing.Count4xx + (is4xx ? 1 : 0),
            Count5xx = existing.Count5xx + (is5xx ? 1 : 0),
            Count404 = existing.Count404 + (is404 ? 1 : 0),
            AuthFailures = existing.AuthFailures + (op.StatusCode is 401 or 403 ? 1 : 0),
            HoneypotHits = existing.HoneypotHits + (op.IsHoneypotHit ? 1 : 0),
            NotFoundPaths = is404 && op.Path is { Length: > 0 }
                ? existing.NotFoundPaths.Add(op.Path)
                : existing.NotFoundPaths
        };
    }

    private static (bool is2xx, bool is3xx, bool is4xx, bool is5xx, bool is404) Classify(int statusCode) => (
        statusCode is >= 200 and < 300,
        statusCode is >= 300 and < 400,
        statusCode is >= 400 and < 500,
        statusCode is >= 500,
        statusCode == 404);

    /// <summary>Ranks by last-seen so active/recent clients stay resident over idle history.</summary>
    protected override long ColdnessScore(ResponseHistoryAggregate entry) => entry.LastSeenUtc.UtcTicks;

    protected override async ValueTask<ResponseHistoryAggregate?> LoadFromDurableTierAsync(string key, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT first_seen_utc, last_seen_utc, total_count, count_2xx, count_3xx,
                       count_4xx, count_5xx, count_404, unique_404_paths, auth_failures, honeypot_hits
                FROM response_client_history WHERE client_id = @client_id
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@client_id", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new ResponseHistoryAggregate
            {
                ClientId = key,
                FirstSeenUtc = ParseUtc(reader.GetString(0)),
                LastSeenUtc = ParseUtc(reader.GetString(1)),
                TotalCount = reader.GetInt32(2),
                Count2xx = reader.GetInt32(3),
                Count3xx = reader.GetInt32(4),
                Count4xx = reader.GetInt32(5),
                Count5xx = reader.GetInt32(6),
                Count404 = reader.GetInt32(7),
                AuthFailures = reader.GetInt32(9),
                HoneypotHits = reader.GetInt32(10),
                // Cold load: no path set survives, only the count -- see the type's remarks.
                NotFoundPaths = ImmutableHashSet<string>.Empty,
                SeededUniqueNotFoundPathsBaseline = reader.GetInt32(8)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResponseHistory cold load failed for {ClientId}", key);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<ResponseHistoryOp> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Dedupe by client -- many ops in a batch land on the same client under load; the
        // hot tier already holds the merged aggregate, so upsert its current value once.
        var keys = new HashSet<string>(batch.Count, StringComparer.Ordinal);
        foreach (var op in batch) keys.Add(op.ClientId);
        var entries = keys.Select(TryGetHot).Where(e => e is not null).Select(e => e!).ToArray();
        await UpsertEntriesAsync(entries, ct);
    }

    /// <summary>
    ///     Test-only: drain the hot tier's current entries to SQLite synchronously, bypassing the
    ///     channel/drainer. Production code never calls this -- the background drainer persists
    ///     on its own schedule. Mirrors <c>SqlitePathLifecycleStore.FlushAsync</c>'s equivalent
    ///     (this store uses the op-replay path, not the sample-drain path, so the base class's
    ///     <c>FlushDirtyAsync</c> is a no-op here).
    /// </summary>
    internal Task FlushAsync(CancellationToken ct = default) =>
        UpsertEntriesAsync(Snapshot().Select(kv => kv.Value).ToArray(), ct);

    private async Task UpsertEntriesAsync(IReadOnlyList<ResponseHistoryAggregate> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return;
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
                INSERT INTO response_client_history
                    (client_id, first_seen_utc, last_seen_utc, total_count, count_2xx, count_3xx,
                     count_4xx, count_5xx, count_404, unique_404_paths, auth_failures, honeypot_hits)
                VALUES
                    (@client_id, @first_seen, @last_seen, @total, @c2, @c3, @c4, @c5, @c404,
                     @unique404, @auth, @honeypot)
                ON CONFLICT(client_id) DO UPDATE SET
                    last_seen_utc = excluded.last_seen_utc,
                    total_count = excluded.total_count,
                    count_2xx = excluded.count_2xx,
                    count_3xx = excluded.count_3xx,
                    count_4xx = excluded.count_4xx,
                    count_5xx = excluded.count_5xx,
                    count_404 = excluded.count_404,
                    unique_404_paths = excluded.unique_404_paths,
                    auth_failures = excluded.auth_failures,
                    honeypot_hits = excluded.honeypot_hits
                """;
            foreach (var entry in entries)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@client_id", entry.ClientId);
                cmd.Parameters.AddWithValue("@first_seen", entry.FirstSeenUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@last_seen", entry.LastSeenUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@total", entry.TotalCount);
                cmd.Parameters.AddWithValue("@c2", entry.Count2xx);
                cmd.Parameters.AddWithValue("@c3", entry.Count3xx);
                cmd.Parameters.AddWithValue("@c4", entry.Count4xx);
                cmd.Parameters.AddWithValue("@c5", entry.Count5xx);
                cmd.Parameters.AddWithValue("@c404", entry.Count404);
                cmd.Parameters.AddWithValue("@unique404", entry.UniqueNotFoundPathCount);
                cmd.Parameters.AddWithValue("@auth", entry.AuthFailures);
                cmd.Parameters.AddWithValue("@honeypot", entry.HoneypotHits);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

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
            cmd.CommandText = Data.Schema.SchemaLoader.Load("response_client_history");
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static DateTimeOffset ParseUtc(string s) =>
        DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
