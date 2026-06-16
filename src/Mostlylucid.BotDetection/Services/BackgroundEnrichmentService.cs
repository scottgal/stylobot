using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

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
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on
///         <see cref="ChannelReader{T}.ReadAllAsync"/> and fired
///         <c>ProcessRequestAsync</c> per item under a
///         <see cref="SemaphoreSlim"/>-gated fan-out. Now subscribes to
///         <see cref="TickCadence.Tick10s"/> via <see cref="IScheduleCoordinator"/>;
///         each tick drains whatever is available right now via
///         <see cref="ChannelReader{T}.TryRead"/> and fires the same per-item
///         processing under the same shared semaphore. Processing tasks are
///         intentionally fire-and-forget across ticks because the original
///         design was already fire-and-forget; the semaphore bounds concurrency,
///         not the tick interval.
///     </para>
///     <para>
///         Latency budget: enrichment events can sit in the channel up to one
///         tick interval (~10s) before processing starts. The channel is
///         <see cref="BoundedChannelFullMode.DropOldest"/> so a tick that
///         skips while enrichment work fans out is harmless -- the oldest item
///         is the most stale to begin with.
///     </para>
/// </summary>
public sealed class BackgroundEnrichmentService : IDisposable
{
    private readonly Channel<EnrichmentRequest> _channel;
    private readonly ILogger<BackgroundEnrichmentService> _logger;
    private readonly ProjectHoneypotLookupService _honeypotLookup;
    private readonly VerifiedBotRegistry _verifiedBots;
    private readonly IPatternReputationCache _reputationCache;
    private readonly PatternReputationUpdater _updater;
    private readonly BackgroundEnrichmentOptions _options;
    private readonly SemaphoreSlim _semaphore;
    private readonly IDisposable? _subscription;
    private int _disposed;

    private long _totalProcessed;
    private long _totalEnqueued;
    private long _totalFcrDnsVerified;
    private long _totalFcrDnsSpoofed;
    private long _totalHonestBotVerified;
    private long _totalHonestBotMismatched;

    public BackgroundEnrichmentService(
        ILogger<BackgroundEnrichmentService> logger,
        ProjectHoneypotLookupService honeypotLookup,
        VerifiedBotRegistry verifiedBots,
        IPatternReputationCache reputationCache,
        PatternReputationUpdater updater,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null)
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

        // Semaphore is shared across ticks (previously was a method-local inside
        // ExecuteAsync). MaxConcurrency bounds concurrent ProcessRequestAsync
        // invocations regardless of how many ticks fire per second.
        _semaphore = new SemaphoreSlim(_options.MaxConcurrency);

        // Optional so existing direct-construction tests that exercise
        // TryEnqueue / ProcessRequestAsync in isolation keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "BackgroundEnrichmentService",
                CostHint.Medium,
                OnTickAsync);
        }
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

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain any available
    ///     enrichment requests from the channel via
    ///     <see cref="ChannelReader{T}.TryRead"/> (non-blocking) and fire
    ///     per-item <see cref="ProcessRequestAsync"/> under the shared
    ///     concurrency semaphore. Processing is fire-and-forget (matches the
    ///     legacy <c>ExecuteAsync</c> shape) so the tick handler returns
    ///     promptly; the next tick can fire even while previous DNS lookups
    ///     are still completing.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        while (_channel.Reader.TryRead(out var request))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await _semaphore.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            // Fire-and-forget with semaphore release (matches the legacy
            // ExecuteAsync shape); ProcessRequestAsync releases the semaphore
            // in its finally block.
            _ = ProcessRequestAsync(request, _semaphore, ct);
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
        {
            // Known-bot UA didn't match anything in the published-CIDR / FCrDNS
            // registry (Mastodon, Pleroma, Akkoma, MostlylucidBot etc. live
            // here -- they intentionally have no ip_ranges_url because they
            // run on arbitrary cloud IPs). Fall through to the honest-bot
            // rDNS-suffix path: rDNS the client IP, compare against the
            // domain in the UA's +URL claim. Per 2026-06-15 claim-verify-trust
            // gap analysis (Gap #1) -- this used to live inline inside
            // VerifiedBotContributor.CheckHonestBot, gated by
            // skip_when: detection.early_exit, so it never fired. Now lives
            // off the request path and runs unconditionally on enrichment.
            await RunHonestBotEnrichmentAsync(request, ct);
            return;
        }

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

    /// <summary>Honest-bot rDNS suffix-match count since startup (UA-claimed
    /// domain matched the client IP's rDNS). Fediverse instances mostly land
    /// here.</summary>
    public long TotalHonestBotVerified => Interlocked.Read(ref _totalHonestBotVerified);

    /// <summary>Honest-bot rDNS-mismatch count since startup (UA carried a
    /// +URL claim but rDNS resolved to a different domain). Weaker signal
    /// than the known-bot spoof counter -- CDN / shared-hosting noise lives
    /// here.</summary>
    public long TotalHonestBotMismatched => Interlocked.Read(ref _totalHonestBotMismatched);

    /// <summary>
    ///     Honest-bot rDNS-suffix path. The UA carries a <c>+URL</c> fragment
    ///     (Mastodon / Pleroma / Akkoma instance address, transparent
    ///     operator bot) but is not a published-CIDR bot, so the only
    ///     verification channel available is rDNS-after-the-fact. Lives in
    ///     the background sink (per Option B of the 2026-06-15 claim-verify-
    ///     trust gap analysis) so the request path never blocks on DNS.
    ///
    ///     <list type="bullet">
    ///         <item>Suffix match -- honest operator. Write a slightly
    ///               human-leaning reputation entry. Honest bots are still
    ///               bots, just transparent ones; weight is moderate.</item>
    ///         <item>Mismatch -- could be a CDN-fronted real instance OR a
    ///               +URL impersonator. Conservative: write a slight
    ///               bot-leaning entry with low weight so a single rDNS
    ///               miss can't tip the verdict.</item>
    ///         <item>No rDNS at all -- absent PTR is ambiguous, no write.</item>
    ///     </list>
    /// </summary>
    private async Task RunHonestBotEnrichmentAsync(EnrichmentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.UserAgent) || string.IsNullOrEmpty(request.ClientIp))
            return;

        HonestBotResult? honest;
        try
        {
            honest = await _verifiedBots.VerifyHonestBotAsync(request.UserAgent, request.ClientIp, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Honest-bot enrichment threw for IP {Ip} request {RequestId}",
                ProjectHoneypotLookupService.MaskIp(request.ClientIp), request.RequestId);
            return;
        }

        if (honest is null)
            return; // No +URL in UA, or no rDNS, or empty inputs -- nothing to feed back

        var patternId = $"ip:{request.ClientIp}";
        var current = _reputationCache.Get(patternId);

        if (honest.SuffixMatched)
        {
            Interlocked.Increment(ref _totalHonestBotVerified);
            // label=0.30 leans slightly human (honest bots are bots, but
            // friendly ones); weight=0.7 because rDNS-suffix is weaker than
            // a full FCrDNS confirm (no forward-DNS verification).
            var updated = _updater.ApplyEvidence(
                current,
                patternId,
                "IP",
                request.ClientIp,
                label: 0.30,
                evidenceWeight: 0.7);
            _reputationCache.Update(updated);
            _logger.LogInformation(
                "Honest bot verified at {Ip}: UA claims {Domain} and rDNS confirmed ({Hostname}); reputation -> {Score:F2}",
                ProjectHoneypotLookupService.MaskIp(request.ClientIp),
                honest.ClaimedDomain,
                honest.ResolvedHostname,
                updated.BotScore);
            return;
        }

        Interlocked.Increment(ref _totalHonestBotMismatched);
        // label=0.55 leans slightly bot (rDNS doesn't back up the claim) but
        // weight=0.4 is conservative -- a CDN-fronted Mastodon instance
        // legitimately resolves to amazonaws / cloudflare hostnames, and we
        // do not want one rDNS miss to tip the verdict by itself.
        var mismatchUpdated = _updater.ApplyEvidence(
            current,
            patternId,
            "IP",
            request.ClientIp,
            label: 0.55,
            evidenceWeight: 0.4);
        _reputationCache.Update(mismatchUpdated);
        _logger.LogDebug(
            "Honest-bot rDNS mismatch at {Ip}: UA claims {Domain} but rDNS is {Hostname}; reputation -> {Score:F2}",
            ProjectHoneypotLookupService.MaskIp(request.ClientIp),
            honest.ClaimedDomain,
            honest.ResolvedHostname,
            mismatchUpdated.BotScore);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        try { _semaphore.Dispose(); }
        catch { /* already disposed */ }
    }
}