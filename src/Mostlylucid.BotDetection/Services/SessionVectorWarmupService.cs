using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Seeds the Slim* hot caches from SQLite centroid tables on startup.
///     Reads the most-recently-updated rows from each centroid table and calls the
///     corresponding search's AddAsync / ReplaceAllAsync so inference is warm from
///     the first request. If a table is empty (fresh install), the cache stays empty
///     and will build incrementally from live traffic.
/// </summary>
public sealed class SessionVectorWarmupService : BackgroundService
{
    private readonly ISignatureCentroidStore _signatureStore;
    private readonly ISessionCentroidStore _sessionStore;
    private readonly IIntentCentroidStore _intentStore;
    private readonly ISignatureSimilaritySearch _signatureSearch;
    private readonly ISessionVectorSearch _sessionSearch;
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly SelfMaintenanceOptions _opts;
    private readonly ILogger<SessionVectorWarmupService> _logger;

    public SessionVectorWarmupService(
        ISignatureCentroidStore signatureStore,
        ISessionCentroidStore sessionStore,
        IIntentCentroidStore intentStore,
        ISignatureSimilaritySearch signatureSearch,
        ISessionVectorSearch sessionSearch,
        IIntentSimilaritySearch intentSearch,
        IOptions<BotDetectionOptions> options,
        ILogger<SessionVectorWarmupService> logger)
    {
        _signatureStore  = signatureStore;
        _sessionStore    = sessionStore;
        _intentStore     = intentStore;
        _signatureSearch = signatureSearch;
        _sessionSearch   = sessionSearch;
        _intentSearch    = intentSearch;
        _opts            = options.Value.SelfMaintenance;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let DI-registered hosted services (e.g. schema migration) settle.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        _logger.LogInformation(
            "Warming Slim* hot caches from SQLite centroids " +
            "(signature={SigSize}, session={SessSize}, intent={IntentSize})",
            _opts.SignatureCacheSize, _opts.SessionCacheSize, _opts.IntentCacheSize);

        await Task.WhenAll(
            WarmSignaturesAsync(stoppingToken),
            WarmSessionsAsync(stoppingToken),
            WarmIntentsAsync(stoppingToken));
    }

    // -----------------------------------------------------------------------
    // Per-table warmup helpers
    // -----------------------------------------------------------------------

    private async Task WarmSignaturesAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _signatureStore.GetRecentSignaturesAsync(_opts.SignatureCacheSize, ct);
            if (rows.Count == 0)
            {
                _logger.LogDebug("Signature centroid table is empty; skipping signature warmup");
                return;
            }

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break;
                await _signatureSearch.AddAsync(row.Vector, row.SignatureId, row.WasBot, row.Confidence);
            }

            _logger.LogInformation("Warmed {Count} signature vectors from SQLite", rows.Count);
        }
        catch (OperationCanceledException) { /* shutdown during warmup */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signature cache warmup failed; cache will build from live traffic");
        }
    }

    private async Task WarmSessionsAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _sessionStore.GetRecentSessionsAsync(_opts.SessionCacheSize, ct);
            if (rows.Count == 0)
            {
                _logger.LogDebug("Session centroid table is empty; skipping session warmup");
                return;
            }

            // ISessionVectorSearch.ReplaceAllAsync exists on the interface; use it for bulk load
            // to avoid per-row background SQLite write-back (would re-upsert rows we just read).
            var items = rows
                .Select(row => (row.Vector, new SessionVectorMetadata
                {
                    Signature         = row.SignatureId,
                    IsBot             = row.IsBot,
                    BotProbability    = row.BotProbability,
                    Timestamp         = DateTimeOffset.UtcNow,
                    VelocityVector    = row.VelocityVector,
                    VelocityMagnitude = row.VelocityVector != null
                                        ? SessionVectorizer.VelocityMagnitude(row.VelocityVector)
                                        : 0f,
                    VarianceVector      = row.VarianceVector,
                    FrequencyFingerprint = row.FreqFingerprint,
                    CompressionLevel  = row.CompressionLevel,
                    Priority          = row.Priority,
                    ClusterId         = row.ClusterId
                }))
                .ToList();

            await _sessionSearch.ReplaceAllAsync(items);
            _logger.LogInformation("Warmed {Count} session vectors from SQLite", items.Count);
        }
        catch (OperationCanceledException) { /* shutdown during warmup */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session cache warmup failed; cache will build from live traffic");
        }
    }

    private async Task WarmIntentsAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _intentStore.GetRecentIntentsAsync(_opts.IntentCacheSize, ct);
            if (rows.Count == 0)
            {
                _logger.LogDebug("Intent centroid table is empty; skipping intent warmup");
                return;
            }

            foreach (var row in rows)
            {
                if (ct.IsCancellationRequested) break;
                await _intentSearch.AddAsync(row.Vector, row.SignatureId, row.ThreatScore, row.IntentCategory);
            }

            _logger.LogInformation("Warmed {Count} intent vectors from SQLite", rows.Count);
        }
        catch (OperationCanceledException) { /* shutdown during warmup */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intent cache warmup failed; cache will build from live traffic");
        }
    }

}
