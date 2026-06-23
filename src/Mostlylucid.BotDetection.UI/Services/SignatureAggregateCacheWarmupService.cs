using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that rehydrates <see cref="SignatureAggregateCache"/> from the
///     persisted event store on startup.
///     <para>
///     Without this, every restart wiped the cache that feeds the "Top Bots" / "Live
///     Activity" / "live-visitors" widgets, so a fleet rolling under deploy would
///     show empty bot lists until fresh traffic arrived even though the underlying
///     PostgreSQL had years of history. The cache is and remains a read-through
///     hot cache -- this just seeds it from the source of truth on boot so the
///     window of "zero rows after restart" closes.
///     </para>
///     <para>
///     2s startup delay lets the DB connection pool come up; try/catch falls
///     back to "populate from live traffic" if persistence is unreachable.
///     </para>
/// </summary>
public sealed class SignatureAggregateCacheWarmupService : BackgroundService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly SignatureAggregateCache _cache;
    private readonly ILogger<SignatureAggregateCacheWarmupService> _logger;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly bool _isRemoteMode;

    public SignatureAggregateCacheWarmupService(
        IDashboardEventStore eventStore,
        SignatureAggregateCache cache,
        ILogger<SignatureAggregateCacheWarmupService> logger,
        DashboardSourceOptions? sourceOptions = null,
        IFingerprintStore? fingerprintStore = null)
    {
        _eventStore = eventStore;
        _cache = cache;
        _logger = logger;
        _fingerprintStore = fingerprintStore;
        // Presence of DashboardSourceOptions (registered by AddStyloBotDashboardRemote)
        // means this host is a remote-mode dashboard viewer. Dashboard state lives on
        // the gateway in that case ([[project_gateway_data_locality]]); seeding a local
        // cache that nothing writes to just freezes it at startup-warm and produces
        // drift between this surface and the home card. Skip the warmup entirely.
        _isRemoteMode = sourceOptions?.Pull?.Type == DashboardSourceType.Rest;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_isRemoteMode)
        {
            _logger.LogInformation(
                "SignatureAggregateCacheWarmup: skipped (remote-mode host -- gateway owns the cache, dashboard reads via REST)");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            // MaxEntries is the cap; pull at most that many. 24h window covers the
            // typical operator's "what was happening recently" view after a restart.
            var topBots = await _eventStore.GetTopBotsAsync(
                count: _cache.MaxEntries,
                startTime: DateTime.UtcNow.AddHours(-24),
                endTime: DateTime.UtcNow);

            if (topBots.Count > 0)
            {
                _cache.SeedFromTopBots(topBots);
                _logger.LogInformation(
                    "Warmed signature aggregate cache with {Count} signatures from last 24h",
                    topBots.Count);

                // Pull display names from the contract-gated fingerprint store
                // (Fingerprint.DisplayName) and push them into the cache's
                // resolved-name dict. This is the ONLY path that populates names
                // post-2026-06-19-single-source-fingerprint-name spec; the
                // per-detection write path no longer touches names. Skipped when
                // no IFingerprintStore is registered (e.g. legacy fixtures).
                if (_fingerprintStore is not null)
                {
                    try
                    {
                        var sigs = topBots
                            .Select(b => b.PrimarySignature)
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        if (sigs.Count > 0)
                        {
                            var names = await _fingerprintStore.GetDisplayNamesBySignaturesAsync(
                                sigs, stoppingToken);
                            _cache.ApplyResolvedNames(names);
                            _logger.LogInformation(
                                "Pulled {Count} display names from IFingerprintStore for warmed cache entries",
                                names.Count(kv => !string.IsNullOrEmpty(kv.Value)));
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to pull display names from IFingerprintStore -- visitor list will render without names until a refresh succeeds");
                    }
                }

                // Pre-warm the per-signature hit ring buffers from the last hour of stored
                // detections so the Live Activity sparkline column shows real trend on
                // first paint after restart. The cap is generous; on light traffic this
                // returns far fewer rows. Detections come back DESC, walk oldest-first
                // so RecordHit's array-shift math advances minute-by-minute correctly.
                try
                {
                    var recent = await _eventStore.GetDetectionsAsync(new DashboardFilter
                    {
                        StartTime = DateTime.UtcNow.AddMinutes(-60),
                        Limit = 50_000,
                    }, stoppingToken);

                    if (recent.Count > 0)
                    {
                        recent.Reverse();
                        _cache.SeedHitTrendsFromDetections(recent);
                        _logger.LogInformation(
                            "Pre-warmed sparkline ring buffers from {Count} recent detections",
                            recent.Count);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to pre-warm sparkline ring buffers -- they will build up from live traffic over the next hour");
                }
            }
            else
            {
                _logger.LogDebug("No recent top bots found to warm signature aggregate cache");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Fail-soft: cache starts empty, live traffic fills it. No detection-path impact.
            _logger.LogWarning(ex,
                "Failed to warm signature aggregate cache from event store -- will populate from live traffic");
        }
    }
}
