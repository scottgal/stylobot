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

    /// <summary>
    ///     Raw User-Agent from the request. Required for the FCrDNS verified-bot
    ///     lookup -- the registry resolves which bot (Applebot, Yandex, etc.) is
    ///     being claimed via UA pattern, then verifies the client IP via
    ///     reverse-DNS + forward-A lookup. Optional today so callers without UA
    ///     context (legacy enqueue sites, tests) still compile; nullable lets
    ///     us bypass the FCrDNS step when absent rather than no-op silently.
    /// </summary>
    public string? UserAgent { get; init; }
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
    private readonly VerifiedBotRegistry _verifiedBots;
    private readonly IPatternReputationCache _reputationCache;
    private readonly PatternReputationUpdater _updater;
    private readonly BackgroundEnrichmentOptions _options;

    private long _totalProcessed;
    private long _totalEnqueued;
    private long _totalFcrDnsVerified;
    private long _totalFcrDnsSpoofed;

    public BackgroundEnrichmentService(
        ILogger<BackgroundEnrichmentService> logger,
        ProjectHoneypotLookupService honeypotLookup,
        VerifiedBotRegistry verifiedBots,
        IPatternReputationCache reputationCache,
        PatternReputationUpdater updater,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _honeypotLookup = honeypotLookup;
        _verifiedBots = verifiedBots;
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

        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                await semaphore.WaitAsync(stoppingToken);

                // Fire-and-forget with semaphore release
                _ = ProcessRequestAsync(request, semaphore, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
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

            // Run Honeypot and FCrDNS in parallel: independent I/O, neither blocks
            // the request path (we are already off the hot path). FCrDNS is the
            // verified-bot DNS verification for the 8 published-CIDR-less bots
            // (Applebot, Yandex, DuckDuckBot, etc.) -- VerifiedBotRegistry's
            // internal cache de-dupes repeated IPs, so we can run this on every
            // enriched request without blowing up DNS.
            var honeypotTask = _honeypotLookup.LookupIpAsync(request.ClientIp, cts.Token);
            var verifiedBotTask = RunVerifiedBotEnrichmentAsync(request, cts.Token);

            var result = await honeypotTask;
            await verifiedBotTask; // FCrDNS side-effects (reputation writes) inside the helper

            Interlocked.Increment(ref _totalProcessed);

            // Feed result into reputation cache so FastPathReputationContributor
            // picks it up on the next request from this IP
            var patternId = $"ip:{request.ClientIp}";

            if (result is { IsListed: true })
            {
                // Listed IP - determine bot evidence strength from threat score
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
                // Clean IP - record slight human signal
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

    /// <summary>
    ///     Async verified-bot path. Runs FCrDNS (reverse-DNS + forward-A confirm) for
    ///     bots whose vendors don't publish CIDR ranges -- Applebot, Applebot-Extended,
    ///     Amazonbot, DuckDuckBot, Baidu, Yandex, TwitterBot, LinkedInBot. The synchronous
    ///     <c>VerifiedBotInline</c> contributor returns null for these because their
    ///     <c>_ipRanges</c> entry is empty; without this background pass they would
    ///     never get verified at all and the only signal would be UA-pattern match,
    ///     which Bug F established as not enough to trust on its own.
    ///
    ///     Side-effects:
    ///         * Verified GoodBot -> write a strong human-leaning reputation entry
    ///           (the next request from this IP early-exits via FastPathReputation).
    ///         * Spoofed UA      -> write a strong bot-leaning reputation entry so the
    ///           impostor lands in ConfirmedBad on the next request.
    ///         * Unknown UA      -> no reputation write; no-op.
    /// </summary>
    private async Task RunVerifiedBotEnrichmentAsync(EnrichmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.UserAgent) || string.IsNullOrEmpty(request.ClientIp))
            return;

        VerifiedBotResult? verdict;
        try
        {
            verdict = await _verifiedBots.VerifyBotAsync(request.UserAgent, request.ClientIp);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "FCrDNS enrichment threw for IP {Ip} request {RequestId}",
                ProjectHoneypotLookupService.MaskIp(request.ClientIp), request.RequestId);
            return;
        }

        if (verdict is null)
            return; // UA didn't match any verifiable bot pattern -- nothing to feed back

        var patternId = $"ip:{request.ClientIp}";
        var current = _reputationCache.Get(patternId);

        if (verdict.IsVerified)
        {
            Interlocked.Increment(ref _totalFcrDnsVerified);
            // Strong human/goodbot signal: label=0.05 means "very likely not a hostile bot"
            // (the cache stores 0.0=human, 1.0=hostile; verified Applebot is a friendly bot
            // we want allowed). Weight 1.0 because FCrDNS-verified is high-confidence evidence.
            var updated = _updater.ApplyEvidence(
                current,
                patternId,
                "IP",
                request.ClientIp,
                label: 0.05,
                evidenceWeight: 1.0);
            _reputationCache.Update(updated);
            _logger.LogInformation(
                "FCrDNS verified {BotName} at {Ip} for request {RequestId}; reputation -> {Score:F2}",
                verdict.BotName,
                ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                request.RequestId,
                updated.BotScore);
            return;
        }

        // Spoofed: UA matched a known bot whose ranges + FCrDNS were checked and both
        // disagreed with the client IP. Strong bot evidence: label=0.95 + full weight.
        Interlocked.Increment(ref _totalFcrDnsSpoofed);
        var spoofUpdated = _updater.ApplyEvidence(
            current,
            patternId,
            "IP",
            request.ClientIp,
            label: 0.95,
            evidenceWeight: 1.0);
        _reputationCache.Update(spoofUpdated);
        _logger.LogWarning(
            "FCrDNS spoof: {Ip} claims {BotName} but reverse/forward DNS does not confirm; reputation -> {Score:F2}",
            ProjectHoneypotLookupService.MaskIp(request.ClientIp),
            verdict.BotName,
            spoofUpdated.BotScore);
    }

    /// <summary>FCrDNS-verified count since startup (dashboard / health surfaces).</summary>
    public long TotalFcrDnsVerified => Interlocked.Read(ref _totalFcrDnsVerified);

    /// <summary>FCrDNS-detected spoof count since startup.</summary>
    public long TotalFcrDnsSpoofed => Interlocked.Read(ref _totalFcrDnsSpoofed);
}
