using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SQLite <see cref="IDetectionFeedbackStore"/> — the FOSS default, co-located
///     with the rest of the dashboard SQLite state on the gateway. One append per
///     flag; the table self-bootstraps. The commercial PostgreSQL pack replaces this
///     via RemoveAll + AddSingleton when a connection string is configured.
/// </summary>
public sealed class SqliteDetectionFeedbackStore : IDetectionFeedbackStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteDetectionFeedbackStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteDetectionFeedbackStore(
        IOptions<BotDetectionOptions> options,
        ILogger<SqliteDetectionFeedbackStore> logger)
    {
        _connectionString = DashboardDbPath.GetConnectionString(options.Value);
        _logger = logger;
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
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS detection_feedback (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    flagged_at          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                    primary_signature   TEXT,
                    entity_id           TEXT,
                    fingerprint_id      TEXT,
                    bot_probability     REAL,
                    confidence          REAL,
                    risk_band           TEXT,
                    bot_name            TEXT,
                    bot_type            TEXT,
                    country_code        TEXT,
                    user_agent          TEXT,
                    note                TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_detection_feedback_flagged_at
                    ON detection_feedback(flagged_at DESC);
                CREATE INDEX IF NOT EXISTS idx_detection_feedback_signature
                    ON detection_feedback(primary_signature);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> RecordFlagAsync(DetectionFeedbackRecord feedback, CancellationToken ct = default)
    {
        try
        {
            await EnsureInitializedAsync(ct);
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO detection_feedback
                    (primary_signature, entity_id, fingerprint_id,
                     bot_probability, confidence, risk_band, bot_name, bot_type,
                     country_code, user_agent, note)
                VALUES
                    (@sig, @entity, @fp, @prob, @conf, @risk, @name, @type, @country, @ua, @note)
                """;
            cmd.Parameters.AddWithValue("@sig",     (object?)feedback.PrimarySignature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@entity",  (object?)feedback.EntityId         ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fp",      (object?)feedback.FingerprintId    ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prob",    (object?)feedback.BotProbability   ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@conf",    (object?)feedback.Confidence       ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@risk",    (object?)feedback.RiskBand         ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@name",    (object?)feedback.BotName          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type",    (object?)feedback.BotType          ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)feedback.CountryCode      ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ua",      (object?)feedback.UserAgent        ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@note",    (object?)feedback.Note             ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DetectionFeedback insert failed for sig={Sig} fp={Fp}",
                feedback.PrimarySignature, feedback.FingerprintId);
            return false;
        }
    }
}