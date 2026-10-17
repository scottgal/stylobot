using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     7×24 day/hour hit-count grid for a single signature, aggregated on
///     demand from the existing requests table. No new schema; uses the
///     idx_requests_sig_time index (signature, timestamp).
///
///     <para>The view feeds the periodicity heatmap on the signature detail
///     page — operators see at-a-glance whether a signature hits "always
///     09:00 Mon-Fri" (cron / business-hours), "uniform across the week"
///     (human-shaped), or "burst then silence" (one-shot crawl).</para>
///
///     <para>FOSS-scoped: one signature at a time, no fingerprint-family
///     aggregation. The commercial trajectory-equivalence layer can stack
///     per-fingerprint families on top using the same shape.</para>
/// </summary>
public sealed class SignaturePeriodicityHeatmap
{
    private readonly BotDetectionOptions _options;
    private readonly ILogger<SignaturePeriodicityHeatmap> _logger;

    public SignaturePeriodicityHeatmap(
        IOptions<BotDetectionOptions> options,
        ILogger<SignaturePeriodicityHeatmap> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Aggregate hits per (day-of-week, hour-of-day) for <paramref name="signatureId"/>
    ///     across the trailing <paramref name="windowDays"/> days. Returns a 7×24 grid
    ///     of ints (rows = day-of-week 0..6 starting Sunday, cols = hour 0..23).
    ///     Empty cells = 0. Returns null when the requests table doesn't exist or the
    ///     query fails — caller renders "no temporal data yet" in that case.
    /// </summary>
    public async Task<HeatmapResult?> ComputeAsync(
        string signatureId, int windowDays = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(signatureId)) return null;

        var dbPath = _options.DatabasePath;
        if (string.IsNullOrEmpty(dbPath)) return null;

        try
        {
            var grid = new int[7, 24];
            var maxValue = 0;
            var totalHits = 0;

            await using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT timestamp FROM requests
                WHERE signature = @sig
                  AND timestamp >= @since
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@since", DateTime.UtcNow.AddDays(-windowDays).ToString("O"));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                // Store writes ISO-8601 with the round-trip "O" format which carries a Z
                // suffix; SQLite gives it back as a string. Parse with AssumeUniversal +
                // AdjustToUniversal so a server in a non-UTC zone doesn't shift the bucket
                // columns by its local offset.
                if (!DateTime.TryParse(
                        reader.GetString(0),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                            | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var ts))
                    continue;
                var dow = (int)ts.DayOfWeek;       // 0 = Sunday … 6 = Saturday
                var hour = ts.Hour;                // 0 … 23
                grid[dow, hour]++;
                totalHits++;
                if (grid[dow, hour] > maxValue) maxValue = grid[dow, hour];
            }

            return new HeatmapResult
            {
                Grid = grid,
                MaxCellValue = maxValue,
                TotalHits = totalHits,
                WindowDays = windowDays
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heatmap aggregation failed for {Sig}", signatureId);
            return null;
        }
    }
}

/// <summary>
///     7×24 grid + max value (so the view can normalise cell intensity to [0,1]
///     without a second pass) + total hits + window size (for "30 days of data"
///     subtitle on the heatmap).
/// </summary>
public sealed class HeatmapResult
{
    /// <summary>[day-of-week 0..6, hour 0..23] = hit count.</summary>
    public required int[,] Grid { get; init; }
    public required int MaxCellValue { get; init; }
    public required int TotalHits { get; init; }
    public required int WindowDays { get; init; }
    public bool HasData => TotalHits > 0;
}
