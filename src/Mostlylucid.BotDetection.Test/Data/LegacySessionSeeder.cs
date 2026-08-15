using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Direct-SQL seeding for the sessions / signatures tables — the LEGACY grain.
///     Phase B of the write-path grain redesign (docs/architecture/write-path-grain-design.md
///     §7.5) retired the write path: <c>AddSessionAsync</c> / <c>UpsertSignatureAsync</c> now
///     FOLD the session summary into the window aggregates instead of writing rows. The
///     tables themselves remain (deliberate DROP is Phase D) and the archive's legacy read
///     surface (GetRecentSessionsAsync / GetSessionsAsync / compaction / eviction) still
///     serves them, so tests that pin those mechanisms seed the rows directly.
/// </summary>
public static class LegacySessionSeeder
{
    public static async Task SeedSessionAsync(
        SqliteDetectionArchive store, PersistedSession session)
    {
        await using var conn = new SqliteConnection(store.PersistenceConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (
                signature, started_at, ended_at, request_count, vector, maturity,
                dominant_state, is_bot, avg_bot_probability, avg_confidence, risk_band,
                action, bot_name, bot_type, country_code, top_reasons_json,
                transition_counts_json, paths_json, avg_processing_time_ms,
                error_count, timing_entropy, narrative,
                header_hashes_json, user_agent_raw,
                frequency_fingerprint, drift_vector
            ) VALUES (
                @sig, @started, @ended, @reqCount, @vector, @maturity,
                @domState, @isBot, @avgProb, @avgConf, @risk,
                @action, @botName, @botType, @country, @reasons,
                @transitions, @paths, @avgTime,
                @errors, @entropy, @narrative,
                @headerHashes, @uaRaw,
                @freqFp, @driftVec
            )
            """;
        cmd.Parameters.AddWithValue("@sig", session.Signature);
        cmd.Parameters.AddWithValue("@started", session.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ended", session.EndedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@reqCount", session.RequestCount);
        cmd.Parameters.AddWithValue("@vector", (object?)session.Vector ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@maturity", session.Maturity);
        cmd.Parameters.AddWithValue("@domState", session.DominantState);
        cmd.Parameters.AddWithValue("@isBot", session.IsBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@avgProb", session.AvgBotProbability);
        cmd.Parameters.AddWithValue("@avgConf", session.AvgConfidence);
        cmd.Parameters.AddWithValue("@risk", session.RiskBand);
        cmd.Parameters.AddWithValue("@action", (object?)session.Action ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@botName", (object?)session.BotName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@botType", (object?)session.BotType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@country", (object?)session.CountryCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reasons", (object?)session.TopReasonsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@transitions", (object?)session.TransitionCountsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@paths", (object?)session.PathsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@avgTime", session.AvgProcessingTimeMs);
        cmd.Parameters.AddWithValue("@errors", session.ErrorCount);
        cmd.Parameters.AddWithValue("@entropy", session.TimingEntropy);
        cmd.Parameters.AddWithValue("@narrative", (object?)session.Narrative ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@headerHashes", (object?)session.HeaderHashesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uaRaw", (object?)session.UserAgentRaw ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@freqFp", (object?)session.FrequencyFingerprintBlob ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@driftVec", (object?)session.DriftVectorBlob ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task SeedSignatureAsync(
        SqliteDetectionArchive store, PersistedSignature signature)
    {
        await using var conn = new SqliteConnection(store.PersistenceConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO signatures (
                signature_id, session_count, total_request_count, first_seen, last_seen,
                is_bot, bot_probability, confidence, risk_band,
                bot_name, bot_type, action, country_code,
                root_vector, root_vector_maturity, narrative, top_reasons_json,
                last_updated_utc
            ) VALUES (
                @id, @sessions, @requests, @first, @last,
                @isBot, @prob, @conf, @risk,
                @botName, @botType, @action, @country,
                @rootVec, @rootMat, @narrative, @reasons,
                @lastUpdatedUtc
            )
            ON CONFLICT(signature_id) DO UPDATE SET
                session_count = session_count + @sessions,
                total_request_count = total_request_count + @requests,
                last_seen = MAX(last_seen, @last),
                bot_probability = @prob,
                confidence = MAX(confidence, @conf),
                risk_band = @risk,
                is_bot = @isBot,
                bot_name = COALESCE(@botName, bot_name),
                bot_type = COALESCE(@botType, bot_type),
                action = COALESCE(@action, action),
                country_code = COALESCE(@country, country_code),
                last_updated_utc = @lastUpdatedUtc
            """;
        cmd.Parameters.AddWithValue("@id", signature.SignatureId);
        cmd.Parameters.AddWithValue("@sessions", signature.SessionCount);
        cmd.Parameters.AddWithValue("@requests", signature.TotalRequestCount);
        cmd.Parameters.AddWithValue("@first", signature.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@last", signature.LastSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@isBot", signature.IsBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@prob", signature.BotProbability);
        cmd.Parameters.AddWithValue("@conf", signature.Confidence);
        cmd.Parameters.AddWithValue("@risk", signature.RiskBand);
        cmd.Parameters.AddWithValue("@botName", (object?)signature.BotName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@botType", (object?)signature.BotType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@action", (object?)signature.Action ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@country", (object?)signature.CountryCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rootVec", (object?)signature.RootVector ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rootMat", signature.RootVectorMaturity);
        cmd.Parameters.AddWithValue("@narrative", (object?)signature.Narrative ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reasons", (object?)signature.TopReasonsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastUpdatedUtc",
            (signature.LastUpdatedUtc ?? DateTime.UtcNow).ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }
}
