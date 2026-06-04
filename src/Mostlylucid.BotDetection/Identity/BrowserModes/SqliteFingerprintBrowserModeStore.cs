using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
                 observation_count, first_seen, last_seen,
                 inferred_archetype, inferred_confidence)
            VALUES (@fp, @mode, @centroid, @maturity, @weights,
                    @obs, @first, @last, @arch, @conf)
            ON CONFLICT(fingerprint_id, mode_id) DO UPDATE SET
                centroid            = excluded.centroid,
                centroid_maturity   = excluded.centroid_maturity,
                weights             = excluded.weights,
                observation_count   = excluded.observation_count,
                last_seen           = excluded.last_seen,
                inferred_archetype  = excluded.inferred_archetype,
                inferred_confidence = excluded.inferred_confidence
            """;
        cmd.Parameters.AddWithValue("@fp", mode.FingerprintId);
        cmd.Parameters.AddWithValue("@mode", mode.ModeId);
        cmd.Parameters.AddWithValue("@centroid", SqliteFingerprintStore.FloatsToBlob(mode.Centroid));
        cmd.Parameters.AddWithValue("@maturity", mode.CentroidMaturity);
        cmd.Parameters.AddWithValue("@weights", SqliteFingerprintStore.FloatsToBlob(mode.Weights));
        cmd.Parameters.AddWithValue("@obs", mode.ObservationCount);
        cmd.Parameters.AddWithValue("@first", mode.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@last", mode.LastSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@arch", (object?)mode.InferredArchetype ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", (object?)mode.InferredConfidence ?? DBNull.Value);
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
                   first_seen, last_seen, inferred_archetype, inferred_confidence
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
                InferredArchetype = reader.IsDBNull(7) ? null : reader.GetString(7),
                InferredConfidence = reader.IsDBNull(8) ? null : reader.GetDouble(8),
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
