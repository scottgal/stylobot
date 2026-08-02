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

        // Cached CATALOGUE bot type. Written alongside cached_bot_probability on the
        // verdict write path so the dashboard's Internal-exclusion + ai/search/tools
        // filters read the real catalogue vocabulary (Internal / SearchEngine / AiBot
        // / Tool / GoodBot / ...) through the LFU, not the inferred_client_type
        // identity axis. NULL on legacy rows and until the first verdict write lands.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN cached_bot_type TEXT", ct);

        // Observation-time UA family. Persisted alongside the vector so the absorption
        // path can pass it to the archetype matcher's UA-family gate; the 2-dim LSH
        // hash baked into the vector is too low-resolution to differentiate hundreds
        // of UA families and lets a Chrome request collide with a "freshping"
        // ghost archetype at high cosine. NULL on legacy rows; the matcher treats
        // null as "no gate" and falls back to unfiltered scoring -- same behavior
        // as before this column existed.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_observations ADD COLUMN ua_family TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_mode_observations ADD COLUMN ua_family TEXT", ct);

        // Multi-domain: (domain, host) owner of each observation row. Nullable —
        // pre-multi-domain rows have no scope and the read paths treat null as
        // "unknown scope" the same way they treat null ua_family.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_observations ADD COLUMN domain TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_observations ADD COLUMN host TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_mode_observations ADD COLUMN domain TEXT", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_mode_observations ADD COLUMN host TEXT", ct);

        // Multi-domain composite indexes on the observation tables. CREATE INDEX
        // IF NOT EXISTS is idempotent; safe to re-run every startup. Placed AFTER
        // the ADD COLUMN calls above so legacy databases (columns don't exist yet)
        // don't trip the index create with "no such column: domain/host".
        // Rationale mirrors the fingerprint_observations / mode_observations read
        // paths: dashboard multi-domain filters need (domain|host, fingerprint_id)
        // to narrow the partition before joining, and (domain, observed_at) covers
        // per-site time-window scans.
        await TryExecuteAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_fp_obs_domain_fp_id " +
            "ON fingerprint_observations(domain, fingerprint_id)", ct);
        await TryExecuteAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_fp_obs_host_fp_id " +
            "ON fingerprint_observations(host, fingerprint_id)", ct);
        await TryExecuteAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_fp_obs_domain_observed_at " +
            "ON fingerprint_observations(domain, observed_at DESC)", ct);
        await TryExecuteAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_fp_mode_obs_domain_fp_mode " +
            "ON fingerprint_mode_observations(domain, fingerprint_id, mode_id)", ct);
        await TryExecuteAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_fp_mode_obs_host_fp_mode " +
            "ON fingerprint_mode_observations(host, fingerprint_id, mode_id)", ct);

        // Phase 3 umbrella-shrinkage (2026-06-21). VarianceMultiplier on each
        // archetype is the calibration-tuned per-archetype tightening factor
        // applied to the matcher's per-dim variance. 1.0 = no narrowing
        // (identical to pre-Phase-3 behaviour); below 1.0 = catchment has been
        // narrowed because drift metrics flagged leakage / bloat. Persisted so
        // the shrinkage survives process restart and the matcher rehydrates
        // the calibrated catchment immediately at boot.
        await TryAddColumnAsync(conn,
            "ALTER TABLE identity_archetypes ADD COLUMN variance_multiplier REAL NOT NULL DEFAULT 1.0", ct);

        // 2026-06-22 -- catalogue_kind discriminator on identity_archetypes
        // lets the same table hold both classic identity archetypes
        // ('identity') and per-mode browser centroids ('browser_mode'),
        // served by the existing calibration + refinement + drainer
        // infrastructure. T11b's ModeCentroidCatalogue is the first reader /
        // writer of the 'browser_mode' rows; legacy rows backfill to
        // 'identity' via the column default.
        await TryAddColumnAsync(conn,
            "ALTER TABLE identity_archetypes ADD COLUMN catalogue_kind TEXT NOT NULL DEFAULT 'identity'", ct);
        // Index on catalogue_kind must be created AFTER the ADD COLUMN above:
        // the schema CREATE INDEX was previously inline in identity_core.sql,
        // but CREATE TABLE IF NOT EXISTS leaves legacy tables intact (no
        // catalogue_kind yet), and the subsequent CREATE INDEX would then
        // fail with "no such column" on databases that pre-date this
        // column. Deferring the index here makes the order safe.
        await using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText =
                "CREATE INDEX IF NOT EXISTS ix_identity_archetypes_catalogue_kind ON identity_archetypes (catalogue_kind)";
            await idxCmd.ExecuteNonQueryAsync(ct);
        }

        // 2026-06-26 -- per spec docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md.
        // Each name-history row carries the projection-input snapshot (UA family/version/os/os_version,
        // observed modifiers, archetype name/kind even though they don't feed the
        // name -- they're useful drift context, observation/member counts, claim
        // status) so old-name -> old-fingerprint-state is one row read.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprint_name_history ADD COLUMN signal_snapshot_json TEXT", ct);

        // 2026-06-27 three-slot name model -- split display_name into induced / llm / given.
        // See docs/superpowers/specs/2026-06-27-fingerprint-name-slots-editor-demo-mode-design.md §5.2.
        // Backfill copies any legacy display_name into induced_name (the safe default;
        // matcher recompose / next LLM pass repopulate the other slots over time).
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN induced_name TEXT", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN induced_name_updated_at TIMESTAMP", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN llm_name TEXT", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN llm_evaluated_at TIMESTAMP", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN llm_description TEXT", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN given_name TEXT", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN given_name_updated_at TIMESTAMP", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprints ADD COLUMN given_name_operator_id TEXT", ct);

        // Legacy display_name was NOT NULL DEFAULT '' on SQLite, so empty-string
        // is the unset marker (NULL-tolerant covers fresh schemas where the
        // column never existed at all).
        await TryExecuteAsync(conn,
            "UPDATE fingerprints SET induced_name = display_name, induced_name_updated_at = display_name_updated_at " +
            "WHERE display_name IS NOT NULL AND display_name <> '' AND induced_name IS NULL", ct);

        await TryExecuteAsync(conn, "ALTER TABLE fingerprints DROP COLUMN display_name", ct);
        await TryExecuteAsync(conn, "ALTER TABLE fingerprints DROP COLUMN display_name_updated_at", ct);

        // 2026-06-27 name-history audit: separate writer-kind from source string.
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprint_name_history ADD COLUMN name_kind TEXT NOT NULL DEFAULT 'induced'", ct);
        await TryAddColumnAsync(conn, "ALTER TABLE fingerprint_name_history ADD COLUMN operator_id TEXT", ct);

        // 2026-06-22 -- drop fingerprint_modes parallel-axis columns. See
        // docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md.
        // The per-mode "inferred archetype" was the parallel-axis bug: every
        // browser-mode row got an identity-archetype label that was nearly
        // always wrong (mastodon-for-everything, then chrome-xhr-for-everything).
        // Bot-raw mode identity is now read from the parent
        // fingerprints.inferred_client_type instead. SQLite ALTER TABLE DROP
        // COLUMN landed in 3.35; the gateway ships >= 3.42. The CREATE in
        // identity_core.sql already omits these columns for fresh schemas; the
        // DROPs below carry forward existing databases.
        await TryDropColumnAsync(conn,
            "ALTER TABLE fingerprint_modes DROP COLUMN inferred_archetype", ct);
        await TryDropColumnAsync(conn,
            "ALTER TABLE fingerprint_modes DROP COLUMN inferred_confidence", ct);

        // Durable, bounded surface-dim drift summary (one fixed row per fingerprint).
        // drift_magnitudes is a BLOB of SurfaceDims.DriftDimCount (7) little-endian floats
        // (per-dim EWMA change magnitude), NULL until the first drift event; drift_frequency
        // is the EWMA change-frequency scalar. Folded ONLY at the session→fingerprint
        // absorption boundary ("write only on change"), like cached_bot_probability is a
        // durable scalar attribute. Migrates existing DBs forward; the CREATE in
        // identity_core.sql already carries these for fresh schemas.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN drift_magnitudes BLOB", ct);
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN drift_frequency REAL NOT NULL DEFAULT 0", ct);

        // 2026-07-16 -- drop cached_risk_band. It stored a value DERIVED from the
        // probability (BucketRisk) as if it were a fact, so a verified good bot at
        // probability 1.0 read VeryHigh (BucketRisk bypasses the composer's verified ->
        // Low friendly-pin). RiskBand is now derived at read via FingerprintRiskProjection.
        // The CREATE in identity_core.sql already omits it for fresh schemas; this carries
        // existing databases forward.
        await TryDropColumnAsync(conn,
            "ALTER TABLE fingerprints DROP COLUMN cached_risk_band", ct);

        // 2026-08-02 -- fast drift-reopen absorption (fp-cache-current architecture,
        // Phase 1). FingerprintDriftService stamps this when weighted-cosine drift
        // crosses DriftWarningThreshold; RecordVerdictWriteBehind/RecordVerdictAsync
        // use the wide DriftReopenAlpha instead of the slow steady-state alpha while
        // now() is before it. NULL = not currently reopened. The CREATE in
        // identity_core.sql already carries this for fresh schemas.
        await TryAddColumnAsync(conn,
            "ALTER TABLE fingerprints ADD COLUMN drift_reopened_until_utc TEXT", ct);
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
    ///     Forward-only ALTER TABLE DROP COLUMN helper. SQLite's DROP COLUMN has no
    ///     <c>IF EXISTS</c>, so the second call after a successful drop raises
    ///     "no such column"; we swallow that to stay idempotent.
    /// </summary>
    private static async Task TryDropColumnAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
        {
            // Already migrated; nothing to do.
        }
    }

    /// <summary>
    ///     Forward-only ALTER TABLE / UPDATE helper that swallows any SqliteException.
    ///     Used for migration statements where the SQLite error surface varies by
    ///     version (DROP COLUMN raises different codes depending on whether the
    ///     column ever existed; an UPDATE that references a missing column raises
    ///     a separate error). For a forward-only migration we want a no-op rather
    ///     than a crash on already-applied state.
    /// </summary>
    private static async Task TryExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException)
        {
            // Forward-only migration; swallow already-applied / not-applicable cases.
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
                observation_count, first_seen, last_seen
            )
            SELECT
                f.fingerprint_id, 'unknown', f.centroid, f.centroid_maturity, f.weights,
                f.observation_count, f.first_seen, f.last_seen
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
