using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Background service that periodically flushes cohort updates from MarkovTracker
///     and snapshots models to persistent storage.
/// </summary>
public sealed class PopulationMarkovService : BackgroundService
{
    private readonly ILogger<PopulationMarkovService> _logger;
    private readonly MarkovTracker _tracker;
    private readonly MarkovOptions _options;

    public PopulationMarkovService(
        ILogger<PopulationMarkovService> logger,
        MarkovTracker tracker,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _tracker = tracker;
        _options = options.Value.Markov;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PopulationMarkovService started (cohort flush every {FlushInterval}s, snapshot every {SnapshotInterval}s)",
            _options.CohortFlushIntervalSeconds, _options.SnapshotIntervalSeconds);

        // Wait for system warmup
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        var lastSnapshot = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Flush pending cohort updates to baselines
                _tracker.FlushCohortUpdates();

                // Periodic snapshot check
                var now = DateTime.UtcNow;
                if ((now - lastSnapshot).TotalSeconds >= _options.SnapshotIntervalSeconds)
                {
                    var stats = _tracker.GetStats();
                    _logger.LogInformation(
                        "Markov state: {Signatures} signatures, {Cohorts} cohorts, " +
                        "global baseline: {Nodes} nodes / {Edges} active edges",
                        stats.ActiveSignatures, stats.CohortCount,
                        stats.GlobalBaselineNodes, stats.GlobalBaselineEdges);

                    // TODO: Snapshot to TimescaleDB via IMarkovSnapshotStore
                    // when PostgreSQL storage is wired up

                    lastSnapshot = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in PopulationMarkovService cycle");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.CohortFlushIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
