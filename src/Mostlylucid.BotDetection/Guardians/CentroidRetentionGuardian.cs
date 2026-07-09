using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Data guardian that prunes stale rows from all three centroid tables
///     (Phase 4 of the old VectorCompactionService).
///
///     Rows older than <see cref="SelfMaintenanceOptions.CentroidRetentionDays"/> are
///     deleted from <see cref="ISignatureCentroidStore"/>,
///     <see cref="ISessionCentroidStore"/>, and <see cref="IIntentCentroidStore"/>
///     in parallel. The cutoff epoch is computed fresh on every run so clock drift
///     between scheduling ticks does not accumulate.
///
///     Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:CentroidRetention:*</c>.
/// </summary>
public sealed class CentroidRetentionGuardian : IGuardian
{
    private readonly ISignatureCentroidStore _signatureCentroidStore;
    private readonly ISessionCentroidStore _sessionCentroidStore;
    private readonly IIntentCentroidStore _intentCentroidStore;
    private readonly SelfMaintenanceOptions _selfMaintenance;
    private readonly ILogger<CentroidRetentionGuardian> _logger;

    public CentroidRetentionGuardian(
        ISignatureCentroidStore signatureCentroidStore,
        ISessionCentroidStore sessionCentroidStore,
        IIntentCentroidStore intentCentroidStore,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<CentroidRetentionGuardian> logger)
    {
        _signatureCentroidStore = signatureCentroidStore;
        _sessionCentroidStore = sessionCentroidStore;
        _intentCentroidStore = intentCentroidStore;
        _selfMaintenance = options.Value.SelfMaintenance;
        _logger = logger;

        var (enabled, interval) = GuardianConfig.Read(
            config, "CentroidRetention", options.Value.Retention.CompactionInterval);

        Enabled = enabled;
        Interval = interval;
    }

    // ── IGuardian ────────────────────────────────────────────────────────────

    public string Name => "CentroidRetention";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-_selfMaintenance.CentroidRetentionDays)
            .ToUnixTimeSeconds();

        try
        {
            ct.ThrowIfCancellationRequested();

            await Task.WhenAll(
                _signatureCentroidStore.PruneSignaturesOlderThanAsync(cutoff, ct),
                _sessionCentroidStore.PruneSessionsOlderThanAsync(cutoff, ct),
                _intentCentroidStore.PruneIntentsOlderThanAsync(cutoff, ct));

            _logger.LogDebug(
                "CentroidRetention: pruned rows older than {CutoffEpoch} (retention={Days}d)",
                cutoff, _selfMaintenance.CentroidRetentionDays);

            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "pruned",
                DurationMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CentroidRetention: prune run failed");
            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "error",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = ex.Message
            };
        }
    }
}
