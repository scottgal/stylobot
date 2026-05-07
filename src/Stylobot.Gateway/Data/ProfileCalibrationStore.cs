using Microsoft.Data.Sqlite;

namespace Stylobot.Gateway.Data;

public record ProfileCalibrationEntry
{
    public required string SignatureHash { get; init; }
    public required double BotProbability { get; init; }
    public required string RiskBand { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public string? TopDetector { get; init; }
    public required string PathPattern { get; init; }
}

public record ScoreDistributionResult
{
    public long TotalAnalyzed { get; init; }
    public double CollectionPeriodHours { get; init; }
    public Dictionary<string, long> Buckets { get; init; } = new();
}

public record ThresholdSimRow
{
    public double Threshold { get; init; }
    public long WouldBlock { get; init; }
    public double PercentOfTraffic { get; init; }
    public List<string> TopBotTypes { get; init; } = new();
}

public class ProfileCalibrationStore(string dbPath)
{
    private SqliteConnection CreateConnection() =>
        new($"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate");

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS profile_calibration (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                signature_hash TEXT    NOT NULL,
                bot_probability REAL   NOT NULL,
                risk_band      TEXT    NOT NULL,
                bot_type       TEXT,
                bot_name       TEXT,
                top_detector   TEXT,
                path_pattern   TEXT    NOT NULL,
                analyzed_at    TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS idx_pc_probability ON profile_calibration(bot_probability);
            CREATE INDEX IF NOT EXISTS idx_pc_analyzed_at ON profile_calibration(analyzed_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertAsync(ProfileCalibrationEntry entry, CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profile_calibration
                (signature_hash, bot_probability, risk_band, bot_type, bot_name, top_detector, path_pattern)
            VALUES
                ($sig, $prob, $band, $type, $name, $det, $path)
            """;
        cmd.Parameters.AddWithValue("$sig", entry.SignatureHash);
        cmd.Parameters.AddWithValue("$prob", entry.BotProbability);
        cmd.Parameters.AddWithValue("$band", entry.RiskBand);
        cmd.Parameters.AddWithValue("$type", entry.BotType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$name", entry.BotName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$det", entry.TopDetector ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$path", entry.PathPattern);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScoreDistributionResult> GetScoreDistributionAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var statsCmd = conn.CreateCommand();
        statsCmd.CommandText = """
            SELECT COUNT(*),
                   CAST((julianday('now') - julianday(MIN(analyzed_at))) * 24 AS REAL)
            FROM profile_calibration
            """;
        long total = 0;
        double hours = 0;
        await using (var r = await statsCmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                total = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                hours = r.IsDBNull(1) ? 0 : r.GetDouble(1);
            }
        }

        await using var distCmd = conn.CreateCommand();
        distCmd.CommandText = """
            SELECT ROUND(bot_probability, 1) AS bucket, COUNT(*) AS cnt
            FROM profile_calibration
            GROUP BY bucket
            ORDER BY bucket
            """;
        var buckets = new Dictionary<string, long>();
        await using (var r = await distCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                buckets[$"{r.GetDouble(0):F1}"] = r.GetInt64(1);
        }

        return new ScoreDistributionResult
        {
            TotalAnalyzed = total,
            CollectionPeriodHours = Math.Round(hours, 1),
            Buckets = buckets,
        };
    }

    public async Task<List<ThresholdSimRow>> GetThresholdSimulationAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        await using var totalCmd = conn.CreateCommand();
        totalCmd.CommandText = "SELECT COUNT(*) FROM profile_calibration";
        var total = (long)(await totalCmd.ExecuteScalarAsync(ct) ?? 0L);
        if (total == 0) return [];

        var thresholds = new[] { 0.50, 0.60, 0.70, 0.75, 0.80, 0.85, 0.90, 0.95 };
        var rows = new List<ThresholdSimRow>();

        foreach (var threshold in thresholds)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) AS blocked,
                       COALESCE(GROUP_CONCAT(DISTINCT CASE WHEN bot_type IS NOT NULL THEN bot_type END), '') AS types
                FROM profile_calibration
                WHERE bot_probability >= $threshold
                """;
            cmd.Parameters.AddWithValue("$threshold", threshold);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) continue;

            var blocked = r.GetInt64(0);
            var typesRaw = r.GetString(1);
            var topTypes = typesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Distinct()
                .Take(3)
                .ToList();

            rows.Add(new ThresholdSimRow
            {
                Threshold = threshold,
                WouldBlock = blocked,
                PercentOfTraffic = total > 0 ? Math.Round(blocked * 100.0 / total, 1) : 0,
                TopBotTypes = topTypes,
            });
        }

        return rows;
    }

    public async Task<(double Threshold, string Reason)?> GetRecommendedThresholdAsync(CancellationToken ct)
    {
        var dist = await GetScoreDistributionAsync(ct);
        if (dist.TotalAnalyzed < 100) return null;

        var candidates = new[] { 0.4, 0.5, 0.6, 0.7, 0.8 };
        var counts = candidates
            .Select(b => (bucket: b, count: dist.Buckets.GetValueOrDefault($"{b:F1}", 0)))
            .ToList();

        var minBucket = counts.OrderBy(x => x.count).First();
        if (minBucket.count > dist.TotalAnalyzed * 0.05)
            return null;

        var reason = $"Score valley at {minBucket.bucket:F1} separates human and bot clusters.";
        return (minBucket.bucket, reason);
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM profile_calibration";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
