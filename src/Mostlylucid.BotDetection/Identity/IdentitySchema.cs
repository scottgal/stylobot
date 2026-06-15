using Microsoft.Data.Sqlite;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Schema for the metastable fingerprint match system. Seven core tables, plus two vec0
///     virtual indexes when the sqlite-vec extension is loaded. See
///     docs/architecture/fingerprint-match.md.
/// </summary>
internal static class IdentitySchema
{
    /// <summary>
    ///     Creates the seven core tables. Idempotent; safe to call on every
    ///     startup. DDL lives in <c>Data/Schema/identity_core.sql</c> -- it's
    ///     ~140 lines of SQL that diffs / lints / formats much better as a
    ///     real .sql file than as a C# string literal.
    /// </summary>
    public static async Task CreateCoreTablesAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Data.Schema.SchemaLoader.Load("identity_core");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Applies forward-only ALTER TABLE migrations for columns added after the
    ///     initial schema. SQLite's <c>ALTER TABLE ADD COLUMN</c> has no <c>IF NOT EXISTS</c>
    ///     clause, so each migration catches the duplicate-column error to stay
    ///     idempotent.
    /// </summary>
    public static async Task MigrateExistingTablesAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN ambiguity_persistence REAL NOT NULL DEFAULT 0", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN display_name TEXT NOT NULL DEFAULT ''", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN display_name_updated_at TEXT NOT NULL DEFAULT ''", ct);
        // root_centroid: the reference centroid drift is measured against.
        // Seeded at allocation from the matched archetype's centroid (archetypes
        // ARE the cold-start root); replaced by BotClusterService snapshots once
        // the population produces data-driven community means. Each replacement
        // writes a fingerprint_root_history row so the dashboard can show the
        // evolution chain. root_source is a lineage marker
        // (e.g. "archetype:chrome-desktop" or "cluster:abc123").
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN root_centroid BLOB", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN root_centroid_at TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN root_source TEXT", ct);

        // Persistent trust state (gap analysis 2026-06-15, Gap #4). Trust was
        // an in-memory one-way latch on SignatureCoordinator and vanished on
        // process restart; the verifier contributors now read these columns at
        // request entry and short-circuit re-verification while within
        // TrustOptions.TrustCacheTtl. claim_status enumerates the verification
        // state ('unverified' / 'verified' / 'spoofed' / 'behaviourally-trusted');
        // verification_method records the path that verified it; verified_at is
        // the timestamp of first successful verification; trust_observations is
        // a counter for behavioural-trust accumulation (Gap #5 increments).
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN claim_status TEXT NOT NULL DEFAULT 'unverified'", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN verification_method TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN verified_at TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN trust_observations INTEGER NOT NULL DEFAULT 0", ct);
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Already migrated; nothing to do.
        }
    }

    /// <summary>
    ///     Backfills <c>root_centroid</c> for any legacy fingerprint row where the
    ///     column is null (rows inserted before the column existed). Self-seeds from
    ///     the live centroid with source <c>"bootstrap"</c>, and writes a matching
    ///     <c>fingerprint_root_history</c> row so the timeline starts somewhere.
    ///     Runtime contract: every fingerprint has a non-null root_centroid; this
    ///     enforces that on the migration boundary so the dashboard never falls into
    ///     a "calibrating" state.
    /// </summary>
    public static async Task BackfillRootCentroidsAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("O");

        // First write history rows for the legacy fingerprints -- do this BEFORE
        // updating root_centroid so we can filter on `root_centroid IS NULL`.
        await using (var hist = conn.CreateCommand())
        {
            hist.CommandText = """
                INSERT INTO fingerprint_root_history
                    (fingerprint_id, root_centroid, root_source, member_count, set_at)
                SELECT fingerprint_id, centroid, 'bootstrap', 1, @now
                  FROM fingerprints
                 WHERE root_centroid IS NULL
                """;
            hist.Parameters.AddWithValue("@now", now);
            await hist.ExecuteNonQueryAsync(ct);
        }

        await using (var upd = conn.CreateCommand())
        {
            upd.CommandText = """
                UPDATE fingerprints
                   SET root_centroid    = centroid,
                       root_centroid_at = @now,
                       root_source      = 'bootstrap'
                 WHERE root_centroid IS NULL
                """;
            upd.Parameters.AddWithValue("@now", now);
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    ///     Backfills <c>fingerprint_modes</c> with a single synthetic <c>unknown</c>
    ///     mode row for every fingerprint that doesn't already have a mode row.
    ///     The row mirrors the parent fingerprint's centroid / weights / maturity /
    ///     observation_count / first_seen / last_seen verbatim, so existing
    ///     identities are immediately mode-addressable. As real observations
    ///     arrive after this run, the classifier splits them off into proper mode
    ///     rows and the <c>unknown</c> row decays as its observation_count stops
    ///     growing (the prune atom drops it once both gates clear).
    ///
    ///     Idempotent: the <c>NOT EXISTS</c> clause makes re-runs no-ops once the
    ///     fingerprint has any mode row, and the <c>INSERT OR IGNORE</c> guards the
    ///     PRIMARY KEY collision if the row was inserted between the SELECT and the
    ///     INSERT by a concurrent path.
    /// </summary>
    public static async Task SeedFingerprintModesAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO fingerprint_modes (
                fingerprint_id, mode_id, centroid, centroid_maturity, weights,
                observation_count, first_seen, last_seen, inferred_archetype, inferred_confidence
            )
            SELECT
                f.fingerprint_id, 'unknown', f.centroid, f.centroid_maturity, f.weights,
                f.observation_count, f.first_seen, f.last_seen, f.inferred_client_type, f.inferred_type_confidence
              FROM fingerprints f
             WHERE NOT EXISTS (
                   SELECT 1 FROM fingerprint_modes m
                    WHERE m.fingerprint_id = f.fingerprint_id
               )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Creates the vec0 virtual tables when the sqlite-vec extension has been loaded on the
    ///     connection. Caller is responsible for loading the extension first; if it isn't loaded,
    ///     this throws and the caller should fall back to the brute-force engine.
    ///
    ///     The schema carries the fingerprint id as a TEXT primary key on the centroid index,
    ///     and as an auxiliary column (the <c>+</c> prefix means "stored, queryable") on the
    ///     observations index alongside an integer rowid that mirrors
    ///     <c>fingerprint_observations.id</c>. This lets KNN results return fingerprint ids
    ///     directly without a join.
    /// </summary>
    public static async Task CreateVecIndexesAsync(SqliteConnection conn, int dimension, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS fingerprints_vec USING vec0(
                fingerprint_id text primary key,
                centroid float[{dimension}]
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS observations_vec USING vec0(
                observation_id integer primary key,
                +fingerprint_id text,
                vector float[{dimension}]
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
