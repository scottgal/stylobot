using Microsoft.Data.Sqlite;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Schema for the metastable fingerprint match system. Seven core tables, plus two vec0
///     virtual indexes when the sqlite-vec extension is loaded. See
///     docs/architecture/fingerprint-match.md.
/// </summary>
internal static class IdentitySchema
{
    private const string CoreTables = """
        CREATE TABLE IF NOT EXISTS fingerprints (
            fingerprint_id              TEXT PRIMARY KEY,
            centroid                    BLOB NOT NULL,
            centroid_maturity           INTEGER NOT NULL,
            weights                     BLOB NOT NULL,
            member_count                INTEGER NOT NULL,
            observation_count           INTEGER NOT NULL,
            correction_count            INTEGER NOT NULL,
            first_seen                  TEXT NOT NULL,
            last_seen                   TEXT NOT NULL,
            quality                     REAL NOT NULL,
            archetype_origin            TEXT,
            inferred_client_type        TEXT NOT NULL,
            inferred_type_confidence    REAL NOT NULL,
            inferred_type_changed_at    TEXT NOT NULL,
            cached_bot_probability      REAL NOT NULL DEFAULT 0,
            cached_risk_band            TEXT,
            cached_score_updated_at     TEXT,
            ambiguity_persistence       REAL NOT NULL DEFAULT 0,
            display_name                TEXT NOT NULL DEFAULT '',
            display_name_updated_at     TEXT NOT NULL DEFAULT '',
            root_centroid               BLOB,
            root_centroid_at            TEXT,
            root_source                 TEXT
        );

        CREATE TABLE IF NOT EXISTS fingerprint_root_history (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
            root_centroid       BLOB NOT NULL,
            root_source         TEXT NOT NULL,
            member_count        INTEGER NOT NULL DEFAULT 1,
            set_at              TEXT NOT NULL,
            superseded_at       TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_frh_fp_setat
            ON fingerprint_root_history(fingerprint_id, set_at DESC);

        CREATE TABLE IF NOT EXISTS fingerprint_keys (
            primary_signature   TEXT PRIMARY KEY,
            fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
            first_seen          TEXT NOT NULL,
            last_seen           TEXT NOT NULL,
            hit_count           INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_fpk_fp ON fingerprint_keys(fingerprint_id);

        CREATE TABLE IF NOT EXISTS fingerprint_observations (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
            vector              BLOB NOT NULL,
            observed_at         TEXT NOT NULL,
            absorbed_at         TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_fpo_active
            ON fingerprint_observations(fingerprint_id) WHERE absorbed_at IS NULL;

        CREATE TABLE IF NOT EXISTS fingerprint_corrections (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            request_id          TEXT NOT NULL,
            primary_signature   TEXT NOT NULL,
            pass1_fingerprint   TEXT,
            pass2_fingerprint   TEXT NOT NULL REFERENCES fingerprints(fingerprint_id),
            differentiator      BLOB NOT NULL,
            observed_at         TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS identity_dimension_weights (
            id                  INTEGER PRIMARY KEY CHECK (id = 1),
            weights             BLOB NOT NULL,
            samples_used        INTEGER NOT NULL,
            clusters_used       INTEGER NOT NULL,
            archetypes_used     INTEGER NOT NULL,
            last_computed_at    TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS identity_archetypes (
            archetype_id        TEXT PRIMARY KEY,
            name                TEXT NOT NULL,
            description         TEXT,
            centroid            BLOB NOT NULL,
            dimension_mask      BLOB NOT NULL,
            archetype_kind      TEXT NOT NULL,
            descendant_count    INTEGER NOT NULL,
            last_refined_at     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS identity_vector_layout (
            id                  INTEGER PRIMARY KEY CHECK (id = 1),
            version             INTEGER NOT NULL,
            dimension           INTEGER NOT NULL,
            layout_json         TEXT NOT NULL,
            installed_at        TEXT NOT NULL
        );

        -- Per-fingerprint browser-mode rows. A fingerprint can hold several mode
        -- centroids — one per request shape the same identity plays during a
        -- session (navigation / xhr / sub-resource / signalr-negotiate / ...).
        -- Each row reuses the existing centroid + weights blob layout; the
        -- parent fingerprints.centroid stays as the rollup (weighted mean of
        -- children, recomputed on a schedule tick). See
        -- docs/architecture/composite-browser-mode-fingerprints.md.
        CREATE TABLE IF NOT EXISTS fingerprint_modes (
            fingerprint_id        TEXT NOT NULL REFERENCES fingerprints(fingerprint_id) ON DELETE CASCADE,
            mode_id               TEXT NOT NULL,
            centroid              BLOB NOT NULL,
            centroid_maturity     INTEGER NOT NULL,
            weights               BLOB NOT NULL,
            observation_count     INTEGER NOT NULL DEFAULT 0,
            first_seen            TEXT NOT NULL,
            last_seen             TEXT NOT NULL,
            inferred_archetype    TEXT,
            inferred_confidence   REAL,
            PRIMARY KEY (fingerprint_id, mode_id)
        );
        CREATE INDEX IF NOT EXISTS ix_fm_last_seen ON fingerprint_modes(last_seen DESC);

        -- Append-only per-mode observation log. Mirrors fingerprint_observations
        -- shape: matcher inserts a row per request (no read, no merge); the
        -- FingerprintModeAbsorptionService drains unabsorbed rows on a tick,
        -- computes the batched EWMA against the cached mode centroid, and
        -- writes one UPSERT per (fingerprint_id, mode_id) tuple per tick. This
        -- closes the race the previous read-modify-write absorb opened — only
        -- the drainer ever modifies the mode centroid, and it processes all of
        -- a tuple's pending observations in one merge.
        CREATE TABLE IF NOT EXISTS fingerprint_mode_observations (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            fingerprint_id      TEXT NOT NULL REFERENCES fingerprints(fingerprint_id) ON DELETE CASCADE,
            mode_id             TEXT NOT NULL,
            vector              BLOB NOT NULL,
            observed_at         TEXT NOT NULL,
            absorbed_at         TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_fmo_active
            ON fingerprint_mode_observations(fingerprint_id, mode_id) WHERE absorbed_at IS NULL;
        """;

    /// <summary>
    ///     Creates the seven core tables. Idempotent; safe to call on every startup.
    /// </summary>
    public static async Task CreateCoreTablesAsync(SqliteConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CoreTables;
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
