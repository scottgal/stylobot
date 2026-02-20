using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Request to enqueue for background enrichment.
/// </summary>
public record EnrichmentRequest
{
    public required string ClientIp { get; init; }
    public required string SignatureHash { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public required string RequestId { get; init; }
}

/// <summary>
///     Background service that runs expensive detectors (Project Honeypot DNS lookups)
///     asynchronously after detection completes. Uses a bounded Channel with DropOldest
///     backpressure. Results feed into the reputation system so the next request from
///     the same IP benefits immediately.
///
///     This is the first step toward a general tiered detection architecture:
///     fast path produces verdict -> low confidence triggers background enrichment ->
///     results improve future verdicts.
/// </summary>
public class BackgroundEnrichmentService : BackgroundService
{
    private readonly Channel<EnrichmentRequest> _channel;
    private readonly ILogger<BackgroundEnrichmentService> _logger;
    private readonly ProjectHoneypotLookupService _honeypotLookup;
    private readonly IPatternReputationCache _reputationCache;
    private readonly PatternReputationUpdater _updater;
    private readonly BackgroundEnrichmentOptions _options;

    private long _totalProcessed;
    private long _totalEnqueued;

    public BackgroundEnrichmentService(
        ILogger<BackgroundEnrichmentService> logger,
        ProjectHoneypotLookupService honeypotLookup,
        IPatternReputationCache reputationCache,
        PatternReputationUpdater updater,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _honeypotLookup = honeypotLookup;
        _reputationCache = reputationCache;
        _updater = updater;
        _options = options.Value.BackgroundEnrichment;

        _channel = Channel.CreateBounded<EnrichmentRequest>(
            new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false, // Multiple consumers via SemaphoreSlim
                SingleWriter = false
            });
    }

    /// <summary>Current number of items waiting in the queue.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Total requests processed since startup.</summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    /// <summary>Total requests enqueued since startup.</summary>
    public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);

    /// <summary>
    ///     Try to enqueue an enrichment request. Non-blocking, returns false if channel is full
    ///     (oldest items are dropped automatically via DropOldest).
    /// </summary>
    public bool TryEnqueue(EnrichmentRequest request)
    {
        if (!_honeypotLookup.IsConfigured)
            return false;

        var result = _channel.Writer.TryWrite(request);
        if (result)
        {
            Interlocked.Increment(ref _totalEnqueued);
            _logger.LogDebug(
                "Enqueued background enrichment for {RequestId} (IP={Ip}, prob={Prob:F2}, conf={Conf:F2})",
                request.RequestId,
                ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                request.BotProbability,
                request.Confidence);
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BackgroundEnrichmentService started (capacity={Capacity}, concurrency={Concurrency})",
            _options.ChannelCapacity, _options.MaxConcurrency);

        using var semaphore = new SemaphoreSlim(_options.MaxConcurrency);

        await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            await semaphore.WaitAsync(stoppingToken);

            // Fire-and-forget with semaphore release
            _ = ProcessRequestAsync(request, semaphore, stoppingToken);
        }
    }

    private async Task ProcessRequestAsync(
        EnrichmentRequest request,
        SemaphoreSlim semaphore,
        CancellationToken stoppingToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5)); // Hard timeout per lookup

            var result = await _honeypotLookup.LookupIpAsync(request.ClientIp, cts.Token);

            Interlocked.Increment(ref _totalProcessed);

            // Feed result into reputation cache so FastPathReputationContributor
            // picks it up on the next request from this IP
            var patternId = $"ip:{request.ClientIp}";

            if (result is { IsListed: true })
            {
                // Listed IP — determine bot evidence strength from threat score
                var label = result.ThreatScore switch
                {
                    >= 100 => 0.95,
                    >= 50 => 0.85,
                    >= 25 => 0.70,
                    >= 10 => 0.55,
                    _ => 0.40
                };

                // Reduce evidence weight for older entries
                var evidenceWeight = result.DaysSinceLastActivity switch
                {
                    0 => 1.0,
                    <= 7 => 0.9,
                    <= 30 => 0.7,
                    <= 90 => 0.5,
                    _ => 0.3
                };

                var current = _reputationCache.Get(patternId);
                var updated = _updater.ApplyEvidence(
                    current,
                    patternId,
                    "IP",
                    request.ClientIp,
                    label,
                    evidenceWeight);
                _reputationCache.Update(updated);

                _logger.LogDebug(
                    "Background enrichment: IP {Ip} LISTED in Honeypot (threat={Threat}, type={Type}, days={Days}) for {RequestId}. " +
                    "Reputation updated: score={Score:F2}, state={State}",
                    ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                    result.ThreatScore,
                    result.VisitorType,
                    result.DaysSinceLastActivity,
                    request.RequestId,
                    updated.BotScore,
                    updated.State);
            }
            else
            {
                // Clean IP — record slight human signal
                var current = _reputationCache.Get(patternId);
                if (current != null)
                {
                    var updated = _updater.ApplyEvidence(
                        current,
                        patternId,
                        "IP",
                        request.ClientIp,
                        0.3, // Slight human lean
                        0.3); // Low evidence weight
                    _reputationCache.Update(updated);
                }

                _logger.LogDebug(
                    "Background enrichment: IP {Ip} NOT listed in Honeypot for {RequestId}",
                    ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                    request.RequestId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service shutting down, ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Background enrichment failed for IP {Ip}, request {RequestId}",
                ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                request.RequestId);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
