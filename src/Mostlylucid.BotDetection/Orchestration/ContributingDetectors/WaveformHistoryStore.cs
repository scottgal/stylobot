using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Write op enqueued for batched persistence. Signature rides on every
///     op so the drainer can write to the durable tier without back-querying
///     the hot dict for the key.
/// </summary>
public sealed record WaveformOp(string Signature, RequestSnapshot Snapshot);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey, TValue, TWriteOp}"/>
///     for the per-signature request history that
///     <see cref="BehavioralWaveformContributor"/> uses to drive timing,
///     path, transition, rate, session, and interaction analyses.
///     <para>
///     Replaces the previous <c>IMemoryCache</c> + per-signature lock pattern.
///     The cache violated the project rule "Always use DashboardAggregateCache /
///     SignatureAggregateCache / WriteBehindLfuStore. New IMemoryCache w/o
///     backing store = bug" -- waveform history disappeared on restart and
///     there was no durable trail for offline analysis.
///     </para>
///     <para>
///     Privacy: raw User-Agent strings stay in the hot tier (transient,
///     per-process state) but are hashed before SQLite persistence so the
///     durable tier never holds the full UA. Cold loads return snapshots
///     whose <c>UserAgent</c> field is the hash; analyses that do equality
///     comparison still match because identical UAs hash to identical values.
///     </para>
/// </summary>
public sealed class WaveformHistoryStore
    : WriteBehindLfuStore<string, WaveformHistory, WaveformOp>
{
    public const int MaxSnapshotsPerSignature = 100;
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    public WaveformHistoryStore(
        IOptions<BotDetectionOptions> options,
        ILogger<WaveformHistoryStore> logger)
        : base(
            maxEntries: 4096,
            writeQueueCapacity: 8192,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromMilliseconds(500),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "waveform_history.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public Task InitializeAsync(CancellationToken ct = default) =>
        _initTask ??= InitializeOnceAsync(ct);

    /// <summary>
    ///     Append a snapshot to the signature's history. Returns the
    ///     post-merge immutable history so callers can run analysis on the
    ///     freshly-merged state without a second dict lookup.
    /// </summary>
    public WaveformHistory Record(string signature, RequestSnapshot snapshot) =>
        Record(signature, new WaveformOp(signature, snapshot));

    /// <summary>
    ///     Replace the last snapshot's content class for the given signature.
    ///     Uses a merge op with the same timestamp as the existing last entry,
    ///     so <see cref="MergeIntoExisting"/> overwrites in place and the
    ///     drainer's UPSERT updates the row without inserting a duplicate.
    ///     No-op when no hot entry exists -- we don't cold-load just to amend.
    /// </summary>
    public void UpdateLastContentClass(string signature, ContentClass actual)
    {
        var hot = TryGetHot(signature);
        if (hot is null || hot.Snapshots.Count == 0) return;
        var last = hot.Snapshots[^1];
        if (last.ContentClass == actual) return;
        // Same timestamp triggers the in-place replacement branch in MergeIntoExisting.
        var updated = last with { ContentClass = actual };
        Record(signature, updated);
    }

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS waveform_history (
                signature TEXT NOT NULL,
                ts_ticks INTEGER NOT NULL,
                path TEXT NOT NULL,
                method TEXT NOT NULL,
                status_code INTEGER NOT NULL,
                user_agent_hash TEXT NOT NULL,
                referer_hash TEXT NOT NULL,
                content_class INTEGER NOT NULL,
                PRIMARY KEY (signature, ts_ticks)
            );
            CREATE INDEX IF NOT EXISTS idx_waveform_history_signature_ts
                ON waveform_history(signature, ts_ticks);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // === WriteBehindLfuStore hooks =========================================

    protected override WaveformHistory CreateInitial(string key, WaveformOp op) =>
        new(ImmutableList.Create(op.Snapshot), op.Snapshot.Timestamp.UtcTicks);

    protected override WaveformHistory MergeIntoExisting(string key, WaveformHistory existing, WaveformOp op)
    {
        // If the op's timestamp matches the existing last entry, it's an
        // in-place update (e.g. UpdateLastContentClass repairing the
        // content class after the response is generated). Replace the
        // last entry, leave everything else alone.
        if (existing.Snapshots.Count > 0 && existing.Snapshots[^1].Timestamp == op.Snapshot.Timestamp)
        {
            var replaced = existing.Snapshots.SetItem(existing.Snapshots.Count - 1, op.Snapshot);
            return new WaveformHistory(replaced, op.Snapshot.Timestamp.UtcTicks);
        }

        // Standard append + prune. Drop snapshots older than the window
        // (sliding 30-min TTL, mirrors the original IMemoryCache expiration),
        // then trim to the per-signature cap.
        var cutoff = op.Snapshot.Timestamp - HistoryWindow;
        var pruned = existing.Snapshots;
        while (pruned.Count > 0 && pruned[0].Timestamp < cutoff)
            pruned = pruned.RemoveAt(0);
        pruned = pruned.Add(op.Snapshot);
        while (pruned.Count > MaxSnapshotsPerSignature)
            pruned = pruned.RemoveAt(0);
        return new WaveformHistory(pruned, op.Snapshot.Timestamp.UtcTicks);
    }

    protected override long ColdnessScore(WaveformHistory entry) => entry.LastActivityTicks;

    protected override async ValueTask<WaveformHistory?> LoadFromDurableTierAsync(string key, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - HistoryWindow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ts_ticks, path, method, status_code, user_agent_hash, referer_hash, content_class
            FROM waveform_history
            WHERE signature = $sig AND ts_ticks >= $since
            ORDER BY ts_ticks ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$sig", key);
        cmd.Parameters.AddWithValue("$since", since.UtcTicks);
        cmd.Parameters.AddWithValue("$limit", MaxSnapshotsPerSignature);

        var snapshots = ImmutableList<RequestSnapshot>.Empty;
        long lastTicks = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tsTicks = reader.GetInt64(0);
            snapshots = snapshots.Add(new RequestSnapshot
            {
                Timestamp = new DateTimeOffset(tsTicks, TimeSpan.Zero),
                Path = reader.GetString(1),
                Method = reader.GetString(2),
                StatusCode = reader.GetInt32(3),
                UserAgent = reader.GetString(4),   // hash -- see HashUa note above
                RefererHash = reader.GetString(5),
                ContentClass = (ContentClass)reader.GetInt32(6)
            });
            if (tsTicks > lastTicks) lastTicks = tsTicks;
        }
        return snapshots.IsEmpty ? null : new WaveformHistory(snapshots, lastTicks);
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<WaveformOp> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO waveform_history
                    (signature, ts_ticks, path, method, status_code, user_agent_hash, referer_hash, content_class)
                VALUES ($sig, $ts, $path, $method, $status, $ua, $ref, $cls)
                ON CONFLICT (signature, ts_ticks) DO UPDATE SET
                    path = excluded.path,
                    method = excluded.method,
                    status_code = excluded.status_code,
                    user_agent_hash = excluded.user_agent_hash,
                    referer_hash = excluded.referer_hash,
                    content_class = excluded.content_class
                """;
            var pSig = cmd.CreateParameter(); pSig.ParameterName = "$sig"; cmd.Parameters.Add(pSig);
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
            var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
            var pMethod = cmd.CreateParameter(); pMethod.ParameterName = "$method"; cmd.Parameters.Add(pMethod);
            var pStatus = cmd.CreateParameter(); pStatus.ParameterName = "$status"; cmd.Parameters.Add(pStatus);
            var pUa = cmd.CreateParameter(); pUa.ParameterName = "$ua"; cmd.Parameters.Add(pUa);
            var pRef = cmd.CreateParameter(); pRef.ParameterName = "$ref"; cmd.Parameters.Add(pRef);
            var pCls = cmd.CreateParameter(); pCls.ParameterName = "$cls"; cmd.Parameters.Add(pCls);

            foreach (var op in batch)
            {
                pSig.Value = op.Signature;
                pTs.Value = op.Snapshot.Timestamp.UtcTicks;
                pPath.Value = op.Snapshot.Path;
                pMethod.Value = op.Snapshot.Method;
                pStatus.Value = op.Snapshot.StatusCode;
                pUa.Value = HashUa(op.Snapshot.UserAgent);
                pRef.Value = op.Snapshot.RefererHash;
                pCls.Value = (int)op.Snapshot.ContentClass;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string HashUa(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return "";
        var bytes = Encoding.UTF8.GetBytes(ua);
        return Convert.ToHexString(XxHash64.Hash(bytes)).ToLowerInvariant();
    }
}
