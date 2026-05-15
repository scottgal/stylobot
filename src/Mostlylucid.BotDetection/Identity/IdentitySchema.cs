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
            cached_score_updated_at     TEXT
        );

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
