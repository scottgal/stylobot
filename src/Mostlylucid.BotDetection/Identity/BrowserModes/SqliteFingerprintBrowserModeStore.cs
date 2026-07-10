using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     SQLite-backed <see cref="IFingerprintBrowserModeStore"/>. Same connection
///     string + LFU-façade discipline as
///     <see cref="SqliteFingerprintStore"/> — reads go through an in-process LFU
///     cache keyed on <c>fingerprint_id</c>, writes invalidate the slot and
///     write through to SQLite. The cache holds the complete mode list for one
///     fingerprint per entry; per-mode lookups read the list from cache and
///     pick the row, so the hot path is one dict touch.
///
///     Init is delegated to <see cref="SqliteFingerprintStore.EnsureInitialisedAsync"/>
///     (called via the shared concrete instance); this store assumes the schema
///     and the seed migration have already landed by the time any read or write
///     fires.
/// </summary>
public sealed class SqliteFingerprintBrowserModeStore : IFingerprintBrowserModeStore
{
    private const int CacheMaxEntries = 10_000;

    private readonly SqliteFingerprintStore _parent;
    private readonly string _connectionString;
    private readonly ILogger<SqliteFingerprintBrowserModeStore> _logger;

    private readonly ConcurrentDictionary<string, IReadOnlyList<FingerprintBrowserMode>> _modesByFingerprintId
        = new(StringComparer.Ordinal);
    private long _epoch;

    // Test instrumentation: mode observations that adaptive sampling summarised
    // (mode count + maturity advanced, no detail row). Mirrors
    // SqliteFingerprintStore.SummarisedObservationCount.
    internal long SummarisedModeObservationCount;

    public SqliteFingerprintBrowserModeStore(
        SqliteFingerprintStore parent,
        IOptions<BotDetectionOptions> options,
        ILogger<SqliteFingerprintBrowserModeStore> logger)
    {
        _parent = parent;
        _logger = logger;
        var dbPath = options.Value.DatabasePath
            ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
        var dataDir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        _connectionString = $"Data Source={Path.Combine(dataDir, "fingerprints.db")}";
    }

    public async Task<IReadOnlyList<FingerprintBrowserMode>> GetModesAsync(
        string fingerprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return Array.Empty<FingerprintBrowserMode>();
        if (_modesByFingerprintId.TryGetValue(fingerprintId, out var cached)) return cached;

        await _parent.EnsureInitialisedAsync(ct);
        var snapshotEpoch = System.Threading.Interlocked.Read(ref _epoch);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        var rows = await SelectModesAsync(conn, fingerprintId, ct);

        // Populate guard: if a writer invalidated the slot during the SELECT,
        // skip the populate so a stale snapshot can never overwrite a fresher
        // invalidation. Same epoch pattern SqliteFingerprintStore uses for the
        // L1 cache. See Bug O / P / Q for the original write-up.
        var currentEpoch = System.Threading.Interlocked.Read(ref _epoch);
        if (currentEpoch == snapshotEpoch)
        {
            _modesByFingerprintId[fingerprintId] = rows;
            EvictOldest(_modesByFingerprintId, CacheMaxEntries);
        }
        return rows;
    }

    public async Task<FingerprintBrowserMode?> GetModeAsync(
        string fingerprintId, string modeId, CancellationToken ct = default)
    {
        var rows = await GetModesAsync(fingerprintId, ct);
        for (var i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].ModeId, modeId, StringComparison.OrdinalIgnoreCase))
                return rows[i];
        return null;
    }

    public async Task UpsertModeAsync(FingerprintBrowserMode mode, CancellationToken ct = default)
    {
        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_modes
                (fingerprint_id, mode_id, centroid, centroid_maturity, weights,
                 observation_count, first_seen, last_seen)
            VALUES (@fp, @mode, @centroid, @maturity, @weights,
                    @obs, @first, @last)
            ON CONFLICT(fingerprint_id, mode_id) DO UPDATE SET
                centroid          = excluded.centroid,
                centroid_maturity = excluded.centroid_maturity,
                weights           = excluded.weights,
                observation_count = excluded.observation_count,
                last_seen         = excluded.last_seen
            """;
        cmd.Parameters.AddWithValue("@fp", mode.FingerprintId);
        cmd.Parameters.AddWithValue("@mode", mode.ModeId);
        cmd.Parameters.AddWithValue("@centroid", SqliteFingerprintStore.FloatsToBlob(mode.Centroid));
        cmd.Parameters.AddWithValue("@maturity", mode.CentroidMaturity);
        cmd.Parameters.AddWithValue("@weights", SqliteFingerprintStore.FloatsToBlob(mode.Weights));
        cmd.Parameters.AddWithValue("@obs", mode.ObservationCount);
        cmd.Parameters.AddWithValue("@first", mode.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@last", mode.LastSeen.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        InvalidateModes(mode.FingerprintId);
    }

    public async Task DeleteModeAsync(string fingerprintId, string modeId, CancellationToken ct = default)
    {
        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM fingerprint_modes WHERE fingerprint_id = @fp AND mode_id = @mode";
        cmd.Parameters.AddWithValue("@fp", fingerprintId);
        cmd.Parameters.AddWithValue("@mode", modeId);
        await cmd.ExecuteNonQueryAsync(ct);

        InvalidateModes(fingerprintId);
    }

    /// <summary>
    ///     Append an unabsorbed mode observation. Adaptive forgetting (shared policy with the
    ///     parent store): a confirmatory observation on an already-matured mode is summarised
    ///     (mode count + maturity advance, no detail row) so fingerprint_mode_observations grows
    ///     with novelty, not request volume. Novel observations and observations on
    ///     still-maturing modes keep a full detail row for the drainer to fold.
    /// </summary>
    public async Task RecordModeObservationAsync(
        RequestScope scope,
        string fingerprintId, string modeId, float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId) || string.IsNullOrEmpty(modeId)) return;
        await _parent.EnsureInitialisedAsync(ct);

        if (!await ShouldPersistModeObservationAsync(fingerprintId, modeId, vector, ct))
        {
            await SummariseModeObservationAsync(fingerprintId, modeId, ct);
            return;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_mode_observations
                (fingerprint_id, mode_id, vector, observed_at, absorbed_at, ua_family, domain, host)
            VALUES (@fp, @mode, @vec, @ts, NULL, @ua, @domain, @host)
            """;
        cmd.Parameters.AddWithValue("@fp", fingerprintId);
        cmd.Parameters.AddWithValue("@mode", modeId);
        cmd.Parameters.AddWithValue("@vec", SqliteFingerprintStore.FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@ua", (object?)uaFamily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@domain", (object?)scope.Domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@host", (object?)scope.Host ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Adaptive-forgetting decision for a mode observation. Keeps detail for novel
    ///     observations, for every observation while the mode is still maturing, and whenever
    ///     sampling is disabled or the mode / centroid is not yet comparable. Summarises only a
    ///     confirmatory observation on a matured mode.
    /// </summary>
    private async Task<bool> ShouldPersistModeObservationAsync(
        string fingerprintId, string modeId, float[] vector, CancellationToken ct)
    {
        var opts = _parent.VectorOptions;
        if (!opts.AdaptiveObservationSampling)
            return true;

        var mode = await GetModeAsync(fingerprintId, modeId, ct); // cached, warm from the matcher
        if (mode is null)
            return true; // new mode: bootstrap

        if (mode.CentroidMaturity < opts.AbsorptionMaturityThreshold)
            return true; // still learning the mode shape

        if (mode.Centroid.Length != vector.Length || vector.Length == 0)
            return true;

        var novelty = Math.Clamp(1.0 - BruteForceIdentityAnchorIndex.Cosine(vector, mode.Centroid), 0.0, 2.0);
        return novelty >= opts.ObservationNoveltyKeepThreshold;
    }

    /// <summary>
    ///     Summarise a confirmatory mode observation: advance the mode's aggregate counters
    ///     without writing a detail row or waking the drainer. The maturity bump keeps the fold
    ///     accounting honest so the mode centroid keeps stabilising.
    /// </summary>
    private async Task SummariseModeObservationAsync(string fingerprintId, string modeId, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprint_modes
               SET observation_count = observation_count + 1,
                   centroid_maturity = centroid_maturity + 1,
                   last_seen = @ts
             WHERE fingerprint_id = @fp AND mode_id = @mode
            """;
        cmd.Parameters.AddWithValue("@fp", fingerprintId);
        cmd.Parameters.AddWithValue("@mode", modeId);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        InvalidateModes(fingerprintId);
        System.Threading.Interlocked.Increment(ref SummarisedModeObservationCount);
    }

    public async Task<IReadOnlyList<UnabsorbedModeObservation>> ListUnabsorbedModeObservationsAsync(
        int maxRows, CancellationToken ct = default)
    {
        if (maxRows <= 0) return Array.Empty<UnabsorbedModeObservation>();
        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // ORDER BY (fp, mode, id) so the drainer processes a tuple's rows in
        // arrival order — the EWMA result is otherwise indistinguishable, but
        // grouping in C# is one pass instead of a sort.
        cmd.CommandText = """
            SELECT id, fingerprint_id, mode_id, vector, observed_at, ua_family, domain, host
              FROM fingerprint_mode_observations
             WHERE absorbed_at IS NULL
             ORDER BY fingerprint_id, mode_id, id
             LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@lim", maxRows);

        var rows = new List<UnabsorbedModeObservation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UnabsorbedModeObservation(
                ObservationId: reader.GetInt64(0),
                FingerprintId: reader.GetString(1),
                ModeId: reader.GetString(2),
                Vector: SqliteFingerprintStore.BlobToFloats((byte[])reader.GetValue(3)),
                ObservedAt: DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                UaFamily: reader.IsDBNull(5) ? null : reader.GetString(5),
                Domain: reader.IsDBNull(6) ? null : reader.GetString(6),
                Host: reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return rows;
    }

    public async Task AbsorbModeObservationsAsync(
        FingerprintBrowserMode updated,
        IReadOnlyList<long> observationIds,
        CancellationToken ct = default)
    {
        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var modeCmd = conn.CreateCommand())
        {
            modeCmd.Transaction = tx;
            modeCmd.CommandText = """
                INSERT INTO fingerprint_modes
                    (fingerprint_id, mode_id, centroid, centroid_maturity, weights,
                     observation_count, first_seen, last_seen)
                VALUES (@fp, @mode, @centroid, @maturity, @weights,
                        @obs, @first, @last)
                ON CONFLICT(fingerprint_id, mode_id) DO UPDATE SET
                    centroid          = excluded.centroid,
                    centroid_maturity = excluded.centroid_maturity,
                    weights           = excluded.weights,
                    observation_count = excluded.observation_count,
                    last_seen         = excluded.last_seen
                """;
            modeCmd.Parameters.AddWithValue("@fp", updated.FingerprintId);
            modeCmd.Parameters.AddWithValue("@mode", updated.ModeId);
            modeCmd.Parameters.AddWithValue("@centroid", SqliteFingerprintStore.FloatsToBlob(updated.Centroid));
            modeCmd.Parameters.AddWithValue("@maturity", updated.CentroidMaturity);
            modeCmd.Parameters.AddWithValue("@weights", SqliteFingerprintStore.FloatsToBlob(updated.Weights));
            modeCmd.Parameters.AddWithValue("@obs", updated.ObservationCount);
            modeCmd.Parameters.AddWithValue("@first", updated.FirstSeen.ToString("O"));
            modeCmd.Parameters.AddWithValue("@last", updated.LastSeen.ToString("O"));
            await modeCmd.ExecuteNonQueryAsync(ct);
        }

        if (observationIds.Count > 0)
        {
            await using var obsCmd = conn.CreateCommand();
            obsCmd.Transaction = tx;
            // SQLite doesn't support array params; build an in-place IN clause.
            // observationIds count is bounded by the drainer's batch cap so the
            // statement size never explodes.
            var inClause = string.Join(',', Enumerable.Range(0, observationIds.Count).Select(i => $"@id{i}"));
            obsCmd.CommandText = $"""
                UPDATE fingerprint_mode_observations
                   SET absorbed_at = @ts
                 WHERE id IN ({inClause})
                """;
            obsCmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            for (var i = 0; i < observationIds.Count; i++)
                obsCmd.Parameters.AddWithValue($"@id{i}", observationIds[i]);
            await obsCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        InvalidateModes(updated.FingerprintId);
    }

    public async Task<int> PruneAbsorbedModeObservationsAsync(
        int keepPerFingerprint, CancellationToken ct = default)
    {
        if (keepPerFingerprint < 0) keepPerFingerprint = 0;

        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Victim set: absorbed rows ranked beyond the newest-K per fingerprint by
        // id. Unabsorbed rows (absorbed_at IS NULL) are never in the ranking, so
        // the drainer's only reader (ListUnabsorbedModeObservationsAsync) is never
        // starved. There is no absorbed-row reader to preserve here, so the
        // partition key and K are diagnostic margin, not a correctness constraint.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM fingerprint_mode_observations
             WHERE id IN (
                SELECT id FROM (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY fingerprint_id
                               ORDER BY id DESC
                           ) AS rn
                      FROM fingerprint_mode_observations
                     WHERE absorbed_at IS NOT NULL
                ) WHERE rn > @keep
             )
            """;
        cmd.Parameters.AddWithValue("@keep", keepPerFingerprint);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListFingerprintIdsWithModesAsync(
        int maxRows, CancellationToken ct = default)
    {
        if (maxRows <= 0) return Array.Empty<string>();
        await _parent.EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Distinct fp_ids ordered by the oldest mode-row last_seen so the rollup
        // sweep walks stale fingerprints first. ix_fm_last_seen makes this cheap.
        cmd.CommandText = """
            SELECT fingerprint_id
              FROM fingerprint_modes
             GROUP BY fingerprint_id
             ORDER BY MIN(last_seen) ASC
             LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@lim", maxRows);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    private void InvalidateModes(string fingerprintId)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        _modesByFingerprintId.TryRemove(fingerprintId, out _);
        System.Threading.Interlocked.Increment(ref _epoch);
    }

    private static async Task<IReadOnlyList<FingerprintBrowserMode>> SelectModesAsync(
        SqliteConnection conn, string fingerprintId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT mode_id, centroid, centroid_maturity, weights, observation_count,
                   first_seen, last_seen
              FROM fingerprint_modes
             WHERE fingerprint_id = @fp
             ORDER BY first_seen ASC
            """;
        cmd.Parameters.AddWithValue("@fp", fingerprintId);

        var rows = new List<FingerprintBrowserMode>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new FingerprintBrowserMode
            {
                FingerprintId = fingerprintId,
                ModeId = reader.GetString(0),
                Centroid = SqliteFingerprintStore.BlobToFloats((byte[])reader.GetValue(1)),
                CentroidMaturity = reader.GetInt32(2),
                Weights = SqliteFingerprintStore.BlobToFloats((byte[])reader.GetValue(3)),
                ObservationCount = reader.GetInt32(4),
                FirstSeen = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                LastSeen = DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }
        return rows;
    }

    private static void EvictOldest<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dict, int targetSize) where TKey : notnull
    {
        var overflow = dict.Count - targetSize + (targetSize / 10);
        if (overflow <= 0) return;
        var drops = 0;
        foreach (var kv in dict)
        {
            if (drops++ >= overflow) break;
            dict.TryRemove(kv.Key, out _);
        }
    }
}
