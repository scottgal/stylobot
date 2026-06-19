using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Audit;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Middleware that detects bots and adds detection result to HttpContext.
///     Supports policy-based detection via [BotPolicy] attributes and path patterns.
///     Results are stored in HttpContext.Items for access by downstream middleware, controllers, and views.
/// </summary>
/// <remarks>
///     The following keys are available in HttpContext.Items:
///     <list type="bullet">
///         <item><see cref="BotDetectionResultKey" /> - Full BotDetectionResult object</item>
///         <item><see cref="AggregatedEvidenceKey" /> - Full AggregatedEvidence from orchestrator</item>
///         <item><see cref="IsBotKey" /> - Boolean indicating if request is from a bot</item>
///         <item><see cref="BotConfidenceKey" /> - Double confidence score (0.0-1.0)</item>
///         <item><see cref="BotTypeKey" /> - BotType enum value (nullable)</item>
///         <item><see cref="BotNameKey" /> - String bot name (nullable)</item>
///         <item><see cref="BotCategoryKey" /> - String category from detection reasons (nullable)</item>
///         <item><see cref="DetectionReasonsKey" /> - List of DetectionReason objects</item>
///         <item><see cref="PolicyNameKey" /> - Name of the policy used for detection</item>
///         <item><see cref="PolicyActionKey" /> - PolicyAction taken (if any)</item>
///     </list>
/// </remarks>
public class BotDetectionMiddleware(
    RequestDelegate next,
    ILogger<BotDetectionMiddleware> logger,
    IOptions<BotDetectionOptions> options,
    ILicenseState licenseState,
    CountryReputationTracker? countryTracker = null,
    BotClusterService? clusterService = null,
    ReactiveSignalTracker? reactiveTracker = null,
    Orchestration.ContributingDetectors.BehavioralWaveformContributor? waveformContributor = null,
    MultiFactorSignatureService? signatureService = null,
    SignatureCoordinator? signatureCoordinator = null,
    IApiKeyStore? apiKeyStore = null,
    Services.PipelineLoadSensor? loadSensor = null,
    LoadShedDecision? loadShedDecision = null,
    Services.SignatureVerdictGate? verdictGate = null,
    Services.VarianceWatchdog? watchdog = null,
    PolicyActionDispatcher? policyDispatcher = null)
{
    // Default test mode simulations - used as fallback when options don't contain the mode
    private static readonly Dictionary<string, string> DefaultTestModeSimulations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Human browser
            ["human"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",

            // Generic bots
            ["bot"] = "bot/1.0 (+http://example.com/bot)",

            // Search engine crawlers
            ["googlebot"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            ["bingbot"] = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",

            // Scrapers and tools
            ["scrapy"] = "Scrapy/2.11 (+https://scrapy.org)",
            ["curl"] = "curl/8.4.0",
            ["puppeteer"] =
                "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) HeadlessChrome/120.0.0.0 Safari/537.36",

            // Malicious patterns (known bad actors)
            ["malicious"] = "sqlmap/1.7 (http://sqlmap.org)",

            // AI/LLM crawlers
            ["gptbot"] =
                "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.2; +https://openai.com/gptbot",
            ["claudebot"] = "ClaudeBot/1.0; +https://www.anthropic.com/claude-bot",

            // Social media bots
            ["twitterbot"] = "Twitterbot/1.0",
            ["facebookbot"] = "facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)",

            // Monitoring bots
            ["uptimerobot"] = "UptimeRobot/2.0"
        };

    // Caches PropertyInfo lookups for the GeoLocation duck-type (avoids reflection on every request).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Type, string Name), System.Reflection.PropertyInfo?> GeoLocationPropertyCache = new();

    private readonly ILicenseState _licenseState = licenseState;
    private readonly ILogger<BotDetectionMiddleware> _logger = logger;
    private readonly RequestDelegate _next = next;
    private readonly BotDetectionOptions _options = options.Value;
    private readonly ReactiveSignalTracker? _reactiveTracker = reactiveTracker;
    private readonly Orchestration.ContributingDetectors.BehavioralWaveformContributor? _waveformContributor = waveformContributor;
    private readonly MultiFactorSignatureService? _signatureService = signatureService;
    private readonly SignatureCoordinator? _signatureCoordinator = signatureCoordinator;
    private readonly IApiKeyStore? _apiKeyStore = apiKeyStore;
    private readonly Services.PipelineLoadSensor? _loadSensor = loadSensor;
    private readonly LoadShedDecision? _loadShedDecision = loadShedDecision;
    private readonly Services.SignatureVerdictGate? _verdictGate = verdictGate;
    private readonly Services.VarianceWatchdog? _watchdog = watchdog;
    // Wave 4 architectural-drift remediation: cache the singleton dispatcher
    // ctor-injected, instead of paying for context.RequestServices.GetService
    // on every request. Hosts that didn't AddPolicyDispatcher get null and
    // the dispatch block falls through unchanged.
    private readonly PolicyActionDispatcher? _policyDispatcher = policyDispatcher;

    /// <summary>
    ///     Main middleware entry point. Runs bot detection and handles blocking/throttling.
    ///     Uses the BlackboardOrchestrator for full pipeline detection with policy support.
    /// </summary>
    public async Task InvokeAsync(
        HttpContext context,
        IDetectionOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator,
        BotDetection.Telemetry.BotDetectionInstrumentation? telemetryInstrumentation = null,
        AuditProcessorDispatcher? auditProcessorDispatcher = null)
    {
        _loadSensor?.RecordRequest();

        // Phase 4: hook OnCompleted once so DegradationAtom records every
        // upstream response (status + latency). Adaptive-scaling tier
        // multipliers read straight from this stream.
        var degradationAtom = context.RequestServices.GetService<RateLimit.DegradationAtom>();
        if (degradationAtom is not null)
        {
            var startTicks = Environment.TickCount64;
            var requestPath = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
            context.Response.OnCompleted(() =>
            {
                var latencyMs = Environment.TickCount64 - startTicks;
                degradationAtom.RecordResponse(context.Response.StatusCode, latencyMs, requestPath);
                return Task.CompletedTask;
            });
        }

        // Check if bot detection is globally enabled
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Check for [SkipBotDetection] attribute
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<SkipBotDetectionAttribute>() != null)
        {
            _logger.LogDebug("Skipping bot detection for {Path} (SkipBotDetection attribute)",
                context.Request.Path);
            await _next(context);
            return;
        }

        // Check skip paths from configuration
        if (ShouldSkipPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Rich API key: per-key detection overlay (detection still runs, but with detector exclusions)
        // _apiKeyStore is null in test contexts that inject the store via RequestServices.
        var effectiveApiKeyStore = _apiKeyStore ?? context.RequestServices?.GetService<IApiKeyStore>();
        var apiKeyContext = TryValidateRichApiKey(context, effectiveApiKeyStore);
        if (apiKeyContext != null)
        {
            if (apiKeyContext.DisablesAllDetectors)
            {
                if (!_options.AllowFullDetectorBypassApiKeys)
                {
                    context.Items["BotDetection.ApiKeyRejection"] =
                        new ApiKeyRejection(ApiKeyRejectionReason.Disabled,
                            "Full detector bypass is disabled");
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    _logger.LogWarning(
                        "API key '{KeyName}' requested full detector bypass but AllowFullDetectorBypassApiKeys is false",
                        apiKeyContext.KeyName);
                    return;
                }

                // Key disables all detectors - equivalent to legacy bypass
                context.Items[IsBotKey] = false;
                context.Items[BotProbabilityKey] = 0.0;
                context.Items["BotDetection.ApiKeyBypass"] = true;
                context.Items["BotDetection.ApiKeyContext"] = apiKeyContext;
                _logger.LogDebug("Skipping bot detection for {Path} (API key '{KeyName}' disables all detectors)",
                    context.Request.Path, apiKeyContext.KeyName);
                await _next(context);
                return;
            }

            // Store context for downstream use - detection will run with overlay
            context.Items["BotDetection.ApiKeyContext"] = apiKeyContext;
            _logger.LogDebug("Using API key '{KeyName}' overlay for {Path} (disabled: {Disabled})",
                apiKeyContext.KeyName, context.Request.Path,
                string.Join(", ", apiKeyContext.DisabledDetectors));
        }
        else if (context.Items.ContainsKey("BotDetection.ApiKeyRejection"))
        {
            var rejection = (ApiKeyRejection)context.Items["BotDetection.ApiKeyRejection"]!;

            // In proxy (YARP) flows with TrustUpstreamDetection, the gateway already validated
            // the API key. The downstream website won't have the same key registry, so "NotFound"
            // rejections are expected - don't block, let the upstream trust path handle it.
            if (rejection.Reason == ApiKeyRejectionReason.NotFound && _options.TrustUpstreamDetection)
            {
                _logger.LogDebug(
                    "API key not in local registry but TrustUpstreamDetection is enabled - deferring to upstream for {Path}",
                    context.Request.Path);
                // Clear the rejection so downstream middleware doesn't see a stale rejection
                context.Items.Remove("BotDetection.ApiKeyRejection");
            }
            else
            {
                // Key was genuinely rejected (expired, rate limited, path denied)
                var statusCode = rejection.Reason == ApiKeyRejectionReason.RateLimitExceeded ? 429 : 403;
                context.Response.StatusCode = statusCode;
                _logger.LogWarning("API key rejected: {Reason} ({Detail}) for {Path}",
                    rejection.Reason, rejection.Detail, context.Request.Path);
                return;
            }
        }
        else
        {
            // Legacy API key bypass: trusted keys skip detection entirely
            if (_options.EnableLegacyApiBypassKeys && HasValidApiBypassKey(context))
            {
                context.Items[IsBotKey] = false;
                context.Items[BotProbabilityKey] = 0.0;
                context.Items["BotDetection.ApiKeyBypass"] = true;
                _logger.LogDebug("Skipping bot detection for {Path} (legacy API key bypass)", context.Request.Path);
                await _next(context);
                return;
            }
        }

        // Signature-only paths: prime any internal signature-service caches but skip detection.
        // Nothing in the FOSS pipeline downstream of here reads the signature on these paths
        // (no orchestrator → no AggregatedEvidence). External consumers that previously read
        // HttpContext.Items[PrimarySignatureKey] must now run detection or compute their own.
        if (IsSignatureOnlyPath(context.Request.Path))
        {
            _ = ComputeSignature(context);
            await _next(context);
            return;
        }

        // Fast path: trust upstream detection from gateway proxy
        if (_options.TrustUpstreamDetection && TryHydrateFromUpstream(context))
        {
            // Feed country reputation from upstream detection
            var countryCode = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault();
            if (countryTracker != null && !string.IsNullOrEmpty(countryCode) && countryCode != "LOCAL")
            {
                var prob = context.Items[BotProbabilityKey] is double p ? p : 0.0;
                countryTracker.RecordDetection(countryCode, countryCode, prob > 0.5, prob);
            }

            // Notify cluster service of bot detections
            if (clusterService != null && context.Items[IsBotKey] is true)
                clusterService.NotifyBotDetected();

            // Register response recording for upstream-trusted requests too
            // (captures 404s, errors, etc. for behavioral analysis)
            var upstreamEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence;
            if (upstreamEvidence != null)
            {
                var upstreamStartTime = DateTime.UtcNow;
                var upstreamReq = CaptureRequestSnapshot(context);
                var upstreamBotProbability = upstreamEvidence.BotProbability;
                var upstreamAction = upstreamEvidence.PolicyAction;
                var upstreamAuditCtx = auditProcessorDispatcher?.HasProcessors == true
                    ? auditProcessorDispatcher.BuildContext(context, upstreamEvidence)
                    : null;

                context.Response.OnCompleted(() =>
                {
                    var statusCode = context.Response.StatusCode;
                    var contentLength = context.Response.ContentLength ?? 0;
                    var contentType = context.Response.ContentType;
                    var processingTimeMs = (DateTime.UtcNow - upstreamStartTime).TotalMilliseconds;

                    var signal = BuildResponseSignal(
                        upstreamReq.ClientId, upstreamReq.RequestId, upstreamReq.Path, upstreamReq.Method,
                        statusCode, contentLength, contentType,
                        processingTimeMs, upstreamBotProbability, upstreamAction);

                    if (signal is not null)
                        _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

                    if (upstreamAuditCtx is not null)
                    {
                        var finalAuditCtx = upstreamAuditCtx with
                        {
                            Metadata = upstreamAuditCtx.Metadata with { StatusCode = statusCode }
                        };
                        _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalAuditCtx, CancellationToken.None);
                    }

                    return Task.CompletedTask;
                });
            }

            // Only add the trust marker - the gateway already emits full X-Bot-* response headers
            if (_options.ResponseHeaders.Enabled)
            {
                var prefix = _options.ResponseHeaders.HeaderPrefix;
                context.Response.Headers.TryAdd($"{prefix}Upstream-Trust", "true");
                context.Response.Headers.TryAdd($"{prefix}Processing-Ms", "0.0");
            }

            await _next(context);
            return;
        }

        // Test mode: Allow overriding bot detection via header
        // In test mode, we still run the real pipeline but with a simulated User-Agent
        if (_options.EnableTestMode)
        {
            // ml-bot-test-ua: Custom UA string to test directly
            var customUa = context.Request.Headers["ml-bot-test-ua"].FirstOrDefault();
            if (!string.IsNullOrEmpty(customUa))
            {
                await HandleCustomUaDetection(context, customUa, orchestrator, policyRegistry, actionPolicyRegistry,
                    responseCoordinator, auditProcessorDispatcher);
                return;
            }

            // ml-bot-test-mode: Named test modes (googlebot, scrapy, etc.)
            var testMode = context.Request.Headers["ml-bot-test-mode"].FirstOrDefault();
            if (!string.IsNullOrEmpty(testMode))
            {
                await HandleTestModeWithRealDetection(context, testMode, orchestrator, policyRegistry,
                    actionPolicyRegistry, responseCoordinator, auditProcessorDispatcher);
                return;
            }

            // Query-param trigger (opt-in via TestModeQueryParam). Same code
            // path as the header so demo links can drive presets without
            // custom-header fiddling.
            var queryName = _options.TestModeQueryParam;
            if (!string.IsNullOrEmpty(queryName) &&
                context.Request.Query.TryGetValue(queryName, out var queryMode) &&
                !string.IsNullOrEmpty(queryMode.ToString()))
            {
                await HandleTestModeWithRealDetection(context, queryMode.ToString(), orchestrator,
                    policyRegistry, actionPolicyRegistry, responseCoordinator, auditProcessorDispatcher);
                return;
            }
        }

        // Determine policy to use
        var policy = ResolvePolicy(context, endpoint, policyRegistry);
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();

        // Signature verdict gate: consult the live coordinator before running the
        // full pipeline. Three outcomes:
        //   Skip - cached verdict is fresh and confident; enforce it (subject to
        //          VarianceWatchdog veto) and bypass the orchestrator entirely.
        //   Bias - verdict exists but is below SkipMinConfidence or older; write
        //          fingerprint.prior.* signals so Wave 0 contributors (Task 5) can
        //          inject the verdict as a prior before the pipeline runs.
        //   Miss - no usable verdict; run the pipeline normally.
        //
        // Pre-orchestrator: compute the signature for the verdict gate. The orchestrator's
        // foundation wave will recompute it (cheap, idempotent) and write it to ev.Signals
        // for downstream consumers. Local-var only — no parallel HttpContext.Items store.
        var precomputedSig = ComputeSignature(context);

        if (_verdictGate is not null && _signatureCoordinator is not null && _watchdog is not null)
        {
            var gateDecision = await _verdictGate.DecideAsync(
                precomputedSig, policy.SignatureCache, context.RequestAborted);

            if (gateDecision.Action == GateAction.Skip && gateDecision.Verdict is { } v && precomputedSig is not null)
            {
                var wd = _watchdog.Check(context, precomputedSig, v, policy.SignatureCache.Watchdog);
                if (wd.Tripped)
                {
                    context.Response.Headers["X-StyloBot-VerdictSource"] = "pipeline";
                    context.Response.Headers["X-StyloBot-WatchdogTrip"] = wd.Reason ?? "unknown";
                }
                else
                {
                    // precomputedSig is the real MultiFactorSignatures.PrimarySignature.
                    // Without seeding it into Signals + Items, downstream consumers
                    // (DetectionBroadcastMiddleware.ResolvePrimarySignature,
                    // StyloBotForwardedHeadersMiddleware) fall back to sha256(ip:ua)[..16]
                    // and key the SignatureAggregateCache under that fallback hash —
                    // while the forwarded-headers middleware emits the real hash to the
                    // proxied request, so website-side lookups miss the cache forever
                    // and the "You:" pill stays "Detection pending…" on every
                    // verdict-cache hit.
                    //
                    // Seed from v.LatestSignals (the snapshot from the most recent
                    // pipeline-running observation) so the dashboard's
                    // important_signals chips repopulate on cache hits. Without this,
                    // every Skip-served signature detail page rendered an empty signals
                    // panel even when the underlying pipeline pass had populated
                    // dozens of detector signals (TLS JA4, header order hash,
                    // archetype anchor, drift slot, etc.) -- the whole point of the
                    // detail page disappeared for the ~90% of traffic served from
                    // cache.
                    //
                    // Live overrides (PrimarySignature, UserAgentBotType, UserAgentBotName)
                    // land on top of the snapshot so the live UA matcher's fresh
                    // classification still wins.
                    var cachedSignals = v.LatestSignals is { Count: > 0 }
                        ? new Dictionary<string, object>(v.LatestSignals, StringComparer.Ordinal)
                        : new Dictionary<string, object>(capacity: 3, StringComparer.Ordinal);
                    if (!string.IsNullOrEmpty(precomputedSig))
                    {
                        cachedSignals[SignalKeys.PrimarySignature] = precomputedSig;
                        context.Items[SignalKeys.PrimarySignature] = precomputedSig;
                    }

                    // Re-derive BotType / BotName from the live UA every skip-path hit.
                    // UA pattern matching is microseconds, stateless, and the only signal
                    // that survives the L1 short-circuit. Without this, the cached
                    // PrimaryBotType is whatever was first persisted -- if the early
                    // pipeline ever mis-tagged the row (HeuristicEarly-Scraper-fallback,
                    // pre-cb5444fc), the wrong tag is sticky for the verdict's whole
                    // SkipMaxAge window. Re-running the cheap UA matcher means each skip
                    // hit refreshes the friendly classification (SearchEngine for
                    // googlebot/bingbot, SocialMediaBot for Mastodon, etc.).
                    var liveUa = context.Request.Headers.UserAgent.ToString();
                    var (uaBotTypeStr, uaBotName) = BotPatternLoader.Default.MatchUserAgent(liveUa, WellKnownBotIndex.Default);
                    BotType? cachedPrimaryBotType = null;
                    if (!string.IsNullOrEmpty(uaBotTypeStr)
                        && Enum.TryParse<BotType>(uaBotTypeStr, ignoreCase: true, out var parsedBotType))
                    {
                        cachedPrimaryBotType = parsedBotType;
                        cachedSignals[SignalKeys.UserAgentBotType] = uaBotTypeStr;
                        if (!string.IsNullOrEmpty(uaBotName))
                            cachedSignals[SignalKeys.UserAgentBotName] = uaBotName;
                    }
                    // Seed the raw UA so the composer can self-rescue when the cached
                    // signal snapshot is missing ua.family / ua.os entries (a typical
                    // shape for old verdicts -- see HumanBotNameResolutionTests).
                    if (!string.IsNullOrEmpty(liveUa))
                        cachedSignals[SignalKeys.UserAgent] = liveUa;
                    // Local-network override on the Skip path too: mirrors the Miss-path
                    // BotType.Internal classification in DetectionLedgerExtensions. Without
                    // this, the first request (Miss) classifies a LAN client as Internal,
                    // and every subsequent Skip-served request would re-label the same
                    // signature back to the UA-derived BotType (Tool / Scraper / etc.) --
                    // the dashboard would flip from "Internal -> Allow" to "Tool ->
                    // throttle-tools" between cache miss and hit for the same visitor.
                    // Network position is the trust anchor; UA is the description.
                    var skipIsLocalIp = NetworkHelper.IsLocalIp(context.Connection.RemoteIpAddress);
                    if (skipIsLocalIp)
                        cachedPrimaryBotType = BotType.Internal;

                    // ThreatBand is not stored on SignatureVerdict, so it can't be
                    // restored verbatim. Hardcoding ThreatBand.Low (the original
                    // behaviour) means a confirmed-hostile fingerprint (honeypot hit,
                    // .env scanner, haxxor probe) loses its threat classification on
                    // every Skip until SkipMaxAge elapses. Re-derive from the cached
                    // bot probability via the shared SignatureRiskVerdictComposer
                    // bucketing -- not perfect (the original score isn't reconstructable)
                    // but tracks severity instead of always-Low.
                    var cachedThreatBand = Risk.SignatureRiskVerdictComposer.BucketThreat(v.BotProbability);

                    // Lift the cached threat score to the band's floor so persist sees
                    // a positive number consistent with the band. Without this, the Skip
                    // path writes (ThreatBand=Critical, ThreatScore=null) on every
                    // cache-hit row because the verdict's stored raw threat (often 0
                    // for reputation-latch / honeypot signals) gets nulled by the
                    // broadcast middleware's `score > 0 ? score : null` projection --
                    // and SQLite's @threat parameter then writes 0.0. Mirrors the
                    // P8 lift in DetectionLedgerExtensions for the Miss path.
                    var cachedThreatScore = Math.Max(
                        v.ThreatScore,
                        Risk.SignatureRiskVerdictComposer.ThreatBandFloor(cachedThreatBand));

                    // When the live UA matcher stamps a real bot type (Tool, Scraper,
                    // SearchEngine, etc.) the row IS a bot regardless of what the
                    // cached BotProbability says. The cached probability comes from
                    // an earlier full-pipeline run that may have landed at 0 via an
                    // EarlyExit Whitelisted path; the categorical "is this a bot UA"
                    // judgement comes from the UA pattern catalog, not from a sigmoid
                    // rollup. Without this override the cached fastpath persists
                    // "bot_type=Tool / bot_name=curl / is_bot=false" rows -- the
                    // exact "Tool but Human" disconnect Bug U/V verification
                    // surfaced on staging. Mirrors the CreateEarlyExitResult fix
                    // for the same display-coherence reason.
                    var hasMeaningfulBotType = cachedPrimaryBotType is not null
                                               and not BotType.Unknown;
                    var cachedBotProbability = hasMeaningfulBotType ? 1.0 : v.BotProbability;
                    // Mirror the trusted-and-aligned clamp from the MISS path
                    // (DetectionLedgerExtensions ▸ SignatureRiskVerdictComposer):
                    // when the cached classification is Internal -- skipIsLocalIp
                    // confirmed network position above -- the RiskBand must be Low
                    // regardless of what the cached verdict snapshot recorded. Without
                    // this, the FIRST request hits MISS and the composer's clamp
                    // produces Low; every SUBSEQUENT request hits Skip and reads
                    // v.RiskBand which was captured before the clamp shipped
                    // (or from a pre-routing-fix observation), so 90+% of internal
                    // dashboard traffic stays VeryHigh on the dashboard.
                    var cachedRiskBand = cachedPrimaryBotType == BotType.Internal
                        ? RiskBand.Low
                        : v.RiskBand;

                    // Build a ledger that carries the cached contributions through to
                    // AggregatedEvidence.Contributions -- without this, the
                    // DetectionBroadcastMiddleware path that reads evidence.Contributions
                    // for upstream-trusted detection events sees an empty list on every
                    // Skip-served row, and the dashboard's detector_contributions chips
                    // (the signature detail page's primary panel) render blank. The
                    // contributions list is the WHOLE POINT of the signature detail
                    // page; without it the detail page is just metadata. Skipping
                    // ledger construction when no cached contributions exist is fine --
                    // it keeps the cold-start / first-Skip behaviour identical.
                    DetectionLedger? cachedLedger = null;
                    if (v.LatestContributions is { Count: > 0 })
                    {
                        cachedLedger = new DetectionLedger(
                            requestId: context.TraceIdentifier,
                            fingerprint: precomputedSig);
                        foreach (var contribution in v.LatestContributions)
                            cachedLedger.AddContribution(contribution);
                    }

                    // Mirror the MISS-path resolution order from
                    // DetectionLedgerExtensions.ResolveDisplayName so humans served from
                    // the verdict cache get the same composed names ("Mac Chrome 149")
                    // as cache misses. Reading uaBotName directly here was the bug that
                    // produced blank H1s on every Skip-served human signature page --
                    // MatchUserAgent returns null for real browsers, so PrimaryBotName
                    // was null, which downstream persistence and the read layers had
                    // no way to recover.
                    //   1. identity.display_name signal (matcher-supplied, when present)
                    //   2. live-UA catalog match (the only bot-name path)
                    //   3. FingerprintNameComposer.Compose -- self-rescues from the raw
                    //      UA for human Chrome/Safari/Firefox visitors
                    var cachedIdentityName = cachedSignals.TryGetValue(SignalKeys.IdentityDisplayName, out var idDisplay)
                        ? idDisplay as string : null;
                    var cachedPrimaryBotName = !string.IsNullOrEmpty(cachedIdentityName)
                        ? cachedIdentityName
                        : (!string.IsNullOrEmpty(uaBotName)
                            ? uaBotName
                            : Services.FingerprintNameComposer.Compose(cachedSignals, userAgent: liveUa));

                    var cachedEvidence = new AggregatedEvidence
                    {
                        Ledger = cachedLedger,
                        BotProbability = cachedBotProbability,
                        Confidence = v.Confidence,
                        PriorProbability = v.BotProbability,
                        RequestContributionDelta = 0.0,
                        RiskBand = cachedRiskBand,
                        ThreatBand = cachedThreatBand,
                        ThreatScore = cachedThreatScore,
                        TotalProcessingTimeMs = 0.0,
                        PrimaryBotType = cachedPrimaryBotType,
                        PrimaryBotName = cachedPrimaryBotName,
                        Signals = cachedSignals,
                    };
                    context.Items[AggregatedEvidenceKey] = cachedEvidence;
                    // Behaviour-aware tag helpers already read from AggregatedEvidence,
                    // but HttpContext.IsBot() / GetBotDetectionResult() / IsVerifiedBot()
                    // (the controller-side API documented in the behaviour-aware-ux post)
                    // read from BotDetectionResultKey + IsBotKey instead. Without seeding
                    // those on the verdict-cache short-circuit, every cache-hit visitor
                    // is treated as "not a bot" by controller code -- silently breaking
                    // `if (HttpContext.IsBot()) return RedirectToAction("LoginDenied");`
                    // for returning bot visitors.
                    var cachedIsBot = hasMeaningfulBotType || v.BotProbability > 0.5;
                    context.Items[IsBotKey] = cachedIsBot;
                    context.Items[BotProbabilityKey] = cachedBotProbability;
                    context.Items[BotDetectionResultKey] = new BotDetectionResult
                    {
                        IsBot = cachedIsBot,
                        BotType = cachedIsBot
                            ? (cachedPrimaryBotType ?? BotType.Unknown)
                            : (BotType?)null,
                        ConfidenceScore = v.Confidence,
                        ProcessingTimeMs = 0.0,
                    };
                    // "identity-cache" when the metastable fingerprint cache was the fresher
                    // source; "cache" when the per-signature aggregate was. Lets operators see
                    // when a rotated visitor reused their identity verdict instead of paying
                    // for a fresh pipeline pass.
                    context.Response.Headers["X-StyloBot-VerdictSource"] =
                        v.FromIdentityCache ? "identity-cache" : "cache";
                    if (v.IdentityFingerprintId is not null)
                    {
                        context.Response.Headers["X-StyloBot-IdentityFingerprint"] = v.IdentityFingerprintId;
                        // Also surface to downstream middleware / forwarded headers.
                        // Without this, the YARP-forwarded request carries no
                        // identity for verdict-cache hits and the downstream
                        // dashboard renders "Calibrating" forever.
                        context.Items[SignalKeys.IdentityFingerprintId] = v.IdentityFingerprintId;
                    }

                    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "";
                    var pathStr = context.Request.Path.Value ?? "/";
                    _watchdog.RecordObservation(precomputedSig, clientIp, pathStr);
                    _ = _signatureCoordinator.NotifyObservationAsync(
                        precomputedSig, pathStr, v.BotProbability, context.RequestAborted);

                    // Honeypot override: a cached-benign verdict cannot rescue a known
                    // scanner path. If the tagger flagged this request as a honeypot
                    // hit, divert to the honeypot policy (jittered fake response) even
                    // on the skip path. Tier 2 exemptions were filtered out by the
                    // tagger; if we see a tier tag here, the operator wants the trap.
                    if (context.Items.TryGetValue(Honeypot.HoneypotPathTagger.ItemKeyTier, out var skipTierVal)
                        && skipTierVal is Honeypot.HoneypotTier skipTier
                        && skipTier != Honeypot.HoneypotTier.None)
                    {
                        var honeypotPolicy = actionPolicyRegistry.GetPolicy(
                            Honeypot.HoneypotResponseActionPolicy.PolicyName);
                        if (honeypotPolicy != null)
                        {
                            await honeypotPolicy.ExecuteAsync(context, cachedEvidence, context.RequestAborted);
                            return;
                        }
                    }

                    await _next(context);
                    return;
                }
            }
            else if (gateDecision.Action == GateAction.Bias && gateDecision.Verdict is { } vb)
            {
                // Stash prior signals on context.Items; FingerprintPriorContributor (Task 5)
                // reads them in Wave 0 and injects the prior contribution.
                context.Items[SignalKeys.FingerprintPriorProbability] = vb.BotProbability;
                context.Items[SignalKeys.FingerprintPriorConfidence] = vb.Confidence;
                context.Items[SignalKeys.FingerprintPriorRequestCount] = vb.RequestCount;
                context.Items[SignalKeys.FingerprintPriorAgeSeconds] =
                    (DateTime.UtcNow - vb.LastSeenUtc).TotalSeconds;
                if (vb.IdentityFingerprintId is not null)
                    context.Items[SignalKeys.IdentityFingerprintId] = vb.IdentityFingerprintId;
            }
        }

        // Load-shed gate: at High/Critical load, skip detection per policy.LoadShed.
        // Uses connection-id-hash as the seed so retries from the same client land
        // identically. Sheds emit X-StyloBot-Shed=1 for observability.
        if (_loadShedDecision is not null)
        {
            var loadShedSeed = context.Connection?.Id?.GetHashCode()
                ?? context.Request.Path.Value?.GetHashCode()
                ?? 0;
            if (_loadShedDecision.ShouldShed(policy.LoadShed, loadShedSeed))
            {
                context.Response.Headers["X-StyloBot-Shed"] = "1";
                _logger.LogInformation("Load-shed: skipping detection for {Path}", context.Request.Path);
                await _next(context);
                return;
            }
        }

        // Run full pipeline with orchestrator - always use full detection.
        // Wrapped in try/catch so policy.OnFailure governs the response when the
        // pipeline throws (detector bug, store unavailable, etc.) instead of the
        // request crashing into a generic HTTP 500.
        AggregatedEvidence aggregatedResult;
        try
        {
            aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client aborted; rethrow so ASP.NET handles connection teardown normally.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detection pipeline failed for {Path}; applying policy.OnFailure={Mode}",
                context.Request.Path, policy.OnFailure);

            var fh = HandleDetectionFailureFor(policy);
            var fr = fh.Apply(context, ex);
            if (!fr.ContinuePipeline)
                return;

            // Synthesise a neutral verdict so downstream middleware sees a consistent shape.
            aggregatedResult = new AggregatedEvidence
            {
                BotProbability = 0.0,
                Confidence = 0.0,
                RiskBand = RiskBand.Low,
                ThreatBand = ThreatBand.Low,
                TotalProcessingTimeMs = 0,
            };
        }

        PopulateContextFromAggregated(context, aggregatedResult, policy.Name);

        // The orchestrator's foundation wave already wrote signature.primary and
        // signature.multifactor to aggregatedResult.Signals — see SignatureContributor.

        // Record OTel telemetry (spans, metrics, score journey) if instrumentation is registered
        telemetryInstrumentation?.Record(
            System.Diagnostics.Activity.Current, aggregatedResult, context);

        // Feed country reputation and cluster services with detection results
        FeedDetectionServices(context, aggregatedResult);

        // Register response recording BEFORE any early exits (blocked, action policy, etc.)
        // so all response codes (403, 404, 500) are captured for behavioral analysis.
        // Pre-capture all HttpContext values synchronously - DI scopes and the connection
        // may be tearing down by the time the OnCompleted callback fires.
        var requestStartTime = DateTime.UtcNow;
        var capturedReq = CaptureRequestSnapshot(context);
        var capturedBotProbability = aggregatedResult.BotProbability;
        var capturedAction = aggregatedResult.PolicyAction;
        var capturedSig = Orchestration.SignatureLookup.Primary(aggregatedResult);
        var capturedSigCoordinator = _signatureCoordinator;
        var capturedAuditCtx = auditProcessorDispatcher?.HasProcessors == true
            ? auditProcessorDispatcher.BuildContext(context, aggregatedResult)
            : null;

        context.Response.OnCompleted(() =>
        {
            // Read final evidence from context (may have been boosted by ApplyResponseStatusBoost)
            var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
            var finalAction = finalEvidence.PolicyAction;

            var statusCode = context.Response.StatusCode;
            var contentLength = context.Response.ContentLength ?? 0;
            var contentType = context.Response.ContentType;

            // Read Retry-After here, after HandleThrottle has set it on the response.
            int? retryAfterForTracker = null;
            if (context.Response.Headers.TryGetValue("Retry-After", out var ra)
                && int.TryParse(ra.ToString(), out var raParsed))
                retryAfterForTracker = raParsed;
            var processingTimeMs = (DateTime.UtcNow - requestStartTime).TotalMilliseconds;

            var signal = BuildResponseSignal(
                capturedReq.ClientId, capturedReq.RequestId, capturedReq.Path, capturedReq.Method,
                statusCode, contentLength, contentType,
                processingTimeMs, capturedBotProbability, finalAction);

            if (signal is not null)
                _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

            _waveformContributor?.UpdateResponseContentType(capturedReq.ClientId, contentType);

            if (capturedAuditCtx is not null)
            {
                var finalAuditCtx = capturedAuditCtx with
                {
                    Metadata = capturedAuditCtx.Metadata with { StatusCode = statusCode }
                };
                _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalAuditCtx, CancellationToken.None);
            }

            // Update response bytes in the signature coordinator now that the response is complete.
            // ContentLength may be null for chunked responses; we record 0 in that case.
            if (!string.IsNullOrEmpty(capturedSig))
            {
                if (capturedSigCoordinator is not null)
                    _ = capturedSigCoordinator.RecordResponseBytesAsync(capturedSig, capturedReq.RequestId, contentLength);

                // This intentionally captures our own 429s and 403s: those are exactly what bots react to.
                if (_reactiveTracker is not null && statusCode >= 400)
                    _reactiveTracker.RecordErrorServed(capturedSig, statusCode, capturedReq.Path, retryAfterForTracker);
            }

            return Task.CompletedTask;
        });

        // Log detection result
        LogDetectionResult(context, aggregatedResult, policy.Name);

        // Add response headers if enabled
        if (_options.ResponseHeaders.Enabled) AddResponseHeaders(context, aggregatedResult, policy.Name);

        // API key action policy override (e.g., "logonly" for monitoring keys)
        // Respect the policy's ActionPolicyOverridable flag - locked policies cannot be overridden by API keys
        if (apiKeyContext != null && !string.IsNullOrEmpty(apiKeyContext.ActionPolicyName)
            && policy.ActionPolicyOverridable)
        {
            aggregatedResult = aggregatedResult with
            {
                TriggeredActionPolicyName = apiKeyContext.ActionPolicyName
            };
            context.Items[AggregatedEvidenceKey] = aggregatedResult;
        }

        // Internal-network override: when a request arrives from inside the
        // local network AND presents a valid API key, swap the BotType-mapped
        // policy for the InternalNetworkBotTypeActionPolicies entry (default:
        // Tool/Scraper/Bot/AiBot -> logonly). Catches the service-to-service
        // case where the orchestrator pre-fills "throttle-tools" for the
        // website's HttpClient (which looks like a generic Tool to the UA
        // detector) and the throttle policy holds every API call for ~15 s,
        // starving the dashboard. Detection still ran in full -- only the
        // action choice is turned down. Detect-then-down, never detect-then-skip.
        if (apiKeyContext != null
            && aggregatedResult.PrimaryBotType is not null and not BotType.Unknown
            && aggregatedResult.Signals.TryGetValue(SignalKeys.IpIsLocal, out var ipLocalSig)
            && ipLocalSig is bool isLocalTrusted
            && isLocalTrusted
            && _options.InternalNetworkBotTypeActionPolicies.TryGetValue(
                    aggregatedResult.PrimaryBotType.Value.ToString(), out var internalPolicyName)
            && !string.IsNullOrEmpty(internalPolicyName))
        {
            aggregatedResult = aggregatedResult with
            {
                TriggeredActionPolicyName = internalPolicyName
            };
            context.Items[AggregatedEvidenceKey] = aggregatedResult;
        }

        // License log-only override: when license is expired past grace period,
        // force log-only regardless of any configured action policy.
        if (_licenseState.LogOnly)
        {
            var logOnlyPolicy = actionPolicyRegistry.GetPolicy("logonly");
            if (logOnlyPolicy != null)
            {
                var logOnlyResult = await logOnlyPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
                if (!logOnlyResult.Continue)
                    return;
            }
            await InvokeNextWithResponseMutationAsync(context);
            return;
        }

        // Honeypot tag wins over the default action selection. If the tagger
        // flagged this request as a known scanner path AND no other transition
        // already targeted a specific policy, force the honeypot policy so the
        // jittered fake response runs regardless of the verdict's risk band.
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName)
            && context.Items.TryGetValue(Honeypot.HoneypotPathTagger.ItemKeyTier, out var postTierVal)
            && postTierVal is Honeypot.HoneypotTier postTier
            && postTier != Honeypot.HoneypotTier.None)
        {
            aggregatedResult = aggregatedResult with
            {
                TriggeredActionPolicyName = Honeypot.HoneypotResponseActionPolicy.PolicyName
            };
            context.Items[AggregatedEvidenceKey] = aggregatedResult;
        }

        // Per-endpoint action-policy override. Reads [BotAction("...")] and
        // [BotPolicy("...", ActionPolicy = "...")] off the matched route's
        // metadata. Lower precedence than honeypot / API-key / internal
        // overrides (those have already run); higher than the BotType-mapped
        // fallback below. Detection still runs in full; this only changes
        // what action policy answers once the verdict is in.
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
        {
            var endpointOverride = EndpointActionPolicyResolver.ResolveFromEndpoint(context);
            if (!string.IsNullOrEmpty(endpointOverride))
            {
                aggregatedResult = aggregatedResult with
                {
                    TriggeredActionPolicyName = endpointOverride
                };
                context.Items[AggregatedEvidenceKey] = aggregatedResult;
            }
        }

        // Check for triggered action policy first (takes precedence over built-in actions)
        if (!string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
        {
            var actionPolicy = MaybeShadowForObserveOnly(
                actionPolicyRegistry,
                actionPolicyRegistry.GetPolicy(aggregatedResult.TriggeredActionPolicyName));
            if (actionPolicy != null)
            {
                _logger.LogInformation(
                    "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2})",
                    aggregatedResult.TriggeredActionPolicyName,
                    _options.ObserveOnly ? " [observe-only shadow]" : "",
                    context.Request.Path, aggregatedResult.BotProbability);

                var actionResult = await actionPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);

                if (!actionResult.Continue)
                    // Action policy handled the response - don't continue pipeline
                    return;

                // Action policy allows continuation (e.g., logonly, throttle-stealth).
                // The action policy IS the response strategy - skip ShouldBlockRequest
                // so we don't hard-block after the policy explicitly allowed continuation.
                EnsureMaliciousFallbackMaskPii(context);
                await InvokeNextWithResponseMutationAsync(context);
                ApplyResponseStatusBoost(context);
                return;
            }
            else
            {
                _logger.LogWarning(
                    "Action policy '{ActionPolicy}' not found in registry, falling back to default handling",
                    aggregatedResult.TriggeredActionPolicyName);
            }
        }

        // Fallback: no action policy triggered by transitions but a bot was detected.
        // Resolve in priority order:
        //   1. Per-BotType policy from BotTypeActionPolicies (6.8+: MaliciousBot ->
        //      block-hard, AiBot -> rate-limit-ai, etc).
        //   2. DefaultActionPolicyName fallback.
        // This is the SINGLE chokepoint that runs regardless of which orchestrator
        // (Ephemeral vs Blackboard) populated the evidence; the BlackboardOrchestrator
        // also pre-populates TriggeredActionPolicyName from BotTypeActionPolicies, but
        // EphemeralDetectionOrchestrator doesn't, so we must consult the map here too.
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName)
            && aggregatedResult.BotProbability >= _options.BotThreshold
            && aggregatedResult.EarlyExitVerdict is not (EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted))
#pragma warning restore CS0618
        {
            // Step 1: try the per-BotType map (internal-network bucket wins
            // first when ip.is_local AND a valid API key are both present).
            var resolvedPolicyName = ResolveBotTypeActionPolicy(aggregatedResult, apiKeyContext);
            // Step 2: fall back to DefaultActionPolicyName if no per-type match.
            resolvedPolicyName ??= _options.DefaultActionPolicyName;

            var fallbackPolicy = !string.IsNullOrEmpty(resolvedPolicyName)
                ? MaybeShadowForObserveOnly(
                    actionPolicyRegistry,
                    actionPolicyRegistry.GetPolicy(resolvedPolicyName))
                : null;
            if (fallbackPolicy != null && resolvedPolicyName is not null)
            {
                // Update the evidence so downstream (dashboard, logging) sees the action
                aggregatedResult = aggregatedResult with
                {
                    TriggeredActionPolicyName = resolvedPolicyName
                };
                context.Items[AggregatedEvidenceKey] = aggregatedResult;

                _logger.LogInformation(
                    "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2}, type={BotType})",
                    resolvedPolicyName,
                    _options.ObserveOnly ? " [observe-only shadow]" : "",
                    context.Request.Path, aggregatedResult.BotProbability, aggregatedResult.PrimaryBotType);

                var fallbackResult = await fallbackPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);

                if (!fallbackResult.Continue)
                    return;

                // DefaultActionPolicyName replaces ShouldBlockRequest - continue pipeline
                // after the action (e.g., throttle-stealth adds delay then lets the
                // request through, appearing normal to the bot).
                EnsureMaliciousFallbackMaskPii(context);
                await InvokeNextWithResponseMutationAsync(context);
                ApplyResponseStatusBoost(context);
                return;
            }
        }

        // Policy-stack dispatch: bridges the new PolicyAction record family
        // (Allow / Block / Observe / Tag / Challenge / RateLimit / Throttle)
        // to the HTTP response BEFORE the legacy DetectionPolicyAction enum
        // is consulted. Optional dependency: hosts that didn't register
        // AddPolicyDispatcher boot and behave exactly as before. When the
        // dispatcher returns Handled the response is already shaped and we
        // short-circuit; FallThrough lets the legacy block/throttle/challenge
        // path run unchanged.
        if (_policyDispatcher is not null)
        {
            var dispatchResult = await TryDispatchPolicyStackAsync(
                context, aggregatedResult, _policyDispatcher).ConfigureAwait(false);
            if (dispatchResult == PolicyDispatchResult.Handled)
                return;
        }

        // Determine if we should block/throttle
        var shouldBlock = ShouldBlockRequest(aggregatedResult, policy, policyAttr);

        // Policy stack Allow marker overrides the legacy block decision -- the
        // operator wired up a rule that says "this request is fine"; honour it.
        var policyAllowed = context.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey);

        if (shouldBlock.Block && !policyAllowed)
        {
            await HandleBlockedRequest(context, aggregatedResult, policy, policyAttr, shouldBlock.Action);
            return;
        }

        // Continue pipeline
        EnsureMaliciousFallbackMaskPii(context);
        await InvokeNextWithResponseMutationAsync(context);

        // Fail2ban-style: after response, check status code and boost/reduce detection
        ApplyResponseStatusBoost(context);
    }

    /// <summary>
    ///     Build the request-shaped <see cref="PolicyScope"/> and per-request
    ///     signal bag, then ask <paramref name="dispatcher"/> to pick a
    ///     winning rule. Swallows non-cancellation faults so the dispatcher
    ///     can never take down detection.
    /// </summary>
    private async Task<PolicyDispatchResult> TryDispatchPolicyStackAsync(
        HttpContext context,
        AggregatedEvidence aggregated,
        PolicyActionDispatcher dispatcher)
    {
        try
        {
            var scope = BuildPolicyRequestScope(context);
            var signals = BuildPolicyRequestSignals(context, aggregated);
            return await dispatcher
                .DispatchAsync(context, scope, signals, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "PolicyActionDispatcher threw for {Path}; falling through to legacy enforcement",
                context.Request.Path);
            return PolicyDispatchResult.FallThrough;
        }
    }

    /// <summary>
    ///     Derive the request-shaped <see cref="PolicyScope"/> the resolver
    ///     walks. Host slot is the request's domain/subdomain/path; orthogonal
    ///     slots (Method / Geo / Identity) are populated when the corresponding
    ///     signal is present, so a rule attached to a Geo / Method / Identity
    ///     scope narrows correctly via <c>PolicyScopeMatcher</c>.
    /// </summary>
    private static PolicyScope BuildPolicyRequestScope(HttpContext context)
    {
        var hostHeader = context.Request.Host.Host ?? string.Empty;
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        var (domain, subdomain) = SplitHost(hostHeader);
        HostScope? hostSlot = string.IsNullOrEmpty(domain)
            ? null
            : new HostScope.Endpoint(domain, subdomain, path);
        var method = string.IsNullOrEmpty(context.Request.Method) ? null : context.Request.Method.ToUpperInvariant();
        return new PolicyScope(Host: hostSlot, Method: method);
    }

    /// <summary>
    ///     Build the per-request signal bag fed into the resolver. The
    ///     resolver already overlays scope-derived request.* / geo.country /
    ///     identity.* signals on top; this method's job is to forward the
    ///     orchestrator's aggregated signals AND the canonical host slots so
    ///     <c>PolicyScopeMatcher</c> can narrow rules with explicit Method /
    ///     Geo / Identity axes.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> BuildPolicyRequestSignals(
        HttpContext context,
        AggregatedEvidence aggregated)
    {
        var dict = new Dictionary<string, object?>(aggregated.Signals.Count + 8);
        foreach (var kv in aggregated.Signals)
            dict[kv.Key] = kv.Value;

        var hostHeader = context.Request.Host.Host ?? string.Empty;
        var (domain, subdomain) = SplitHost(hostHeader);
        if (!string.IsNullOrEmpty(domain)) dict[PolicyScopeMatcher.RequestDomainKey] = domain;
        if (!string.IsNullOrEmpty(subdomain)) dict[PolicyScopeMatcher.RequestSubdomainKey] = subdomain;
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        dict[PolicyScopeMatcher.RequestPathKey] = path;
        if (!string.IsNullOrEmpty(context.Request.Method))
            dict[PolicyScopeMatcher.RequestMethodKey] = context.Request.Method.ToUpperInvariant();
        return dict;
    }

    /// <summary>
    ///     Split a host header into (apex domain, subdomain). Apex = last two
    ///     labels; subdomain = everything to the left (empty when the host is
    ///     just the apex). Handles IP literals and single-label hosts by
    ///     treating the whole thing as the domain with an empty subdomain.
    /// </summary>
    private static (string Domain, string Subdomain) SplitHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return (string.Empty, string.Empty);
        // Drop the port if present.
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        if (host.Length == 0) return (string.Empty, string.Empty);
        // IP literals -- treat as the whole thing as the domain.
        if (System.Net.IPAddress.TryParse(host, out _)) return (host, string.Empty);
        var parts = host.Split('.');
        if (parts.Length <= 2) return (host, string.Empty);
        var domain = parts[^2] + "." + parts[^1];
        var subdomain = string.Join('.', parts[..^2]);
        return (domain, subdomain);
    }

    #region Detection Service Feeds

    /// <summary>
    ///     Feeds detection data to CountryReputationTracker and BotClusterService.
    ///     Called after both local and test-mode detections.
    /// </summary>
    private void FeedDetectionServices(HttpContext context, AggregatedEvidence aggregated)
    {
        // Feed country reputation tracker
        if (countryTracker != null)
        {
            string? countryCode = null;
            string? countryName = null;

            // Try blackboard signals first (from GeoContributor if registered)
            if (aggregated.Signals != null)
            {
                if (aggregated.Signals.TryGetValue("geo.country_code", out var ccObj))
                    countryCode = ccObj as string ?? ccObj?.ToString();
                if (aggregated.Signals.TryGetValue("geo.country_name", out var cnObj))
                    countryName = cnObj as string ?? cnObj?.ToString();
            }

            // Fallback: read from GeoRouting middleware context items
            // (used when GeoContributor is not in the detection pipeline)
            if (string.IsNullOrEmpty(countryCode) &&
                context.Items.TryGetValue("GeoLocation", out var geoLocObj) &&
                geoLocObj != null)
            {
                var geoType = geoLocObj.GetType();
                var ccProp = GeoLocationPropertyCache.GetOrAdd(
                    (geoType, "CountryCode"), k => k.Type.GetProperty(k.Name));
                if (ccProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC))
                    countryCode = geoCC;
                var cnProp = GeoLocationPropertyCache.GetOrAdd(
                    (geoType, "CountryName"), k => k.Type.GetProperty(k.Name));
                if (cnProp?.GetValue(geoLocObj) is string geoCN && !string.IsNullOrEmpty(geoCN))
                    countryName = geoCN;
            }

            // Fallback: upstream headers (X-Country from CDN/proxy, CF-IPCountry from Cloudflare)
            if (string.IsNullOrEmpty(countryCode))
            {
                countryCode = context.Request.Headers["X-Country"].FirstOrDefault()
                              ?? context.Request.Headers["CF-IPCountry"].FirstOrDefault();
                if (countryCode is "XX" or "LOCAL" or "" or null)
                    countryCode = null;
            }

            // Fallback: GeoDetection.CountryCode context item
            if (string.IsNullOrEmpty(countryCode) &&
                context.Items.TryGetValue("GeoDetection.CountryCode", out var geoCtx) &&
                geoCtx is string geoCountry && !string.IsNullOrEmpty(geoCountry) && geoCountry != "LOCAL")
            {
                countryCode = geoCountry;
            }

            if (!string.IsNullOrEmpty(countryCode) && countryCode != "LOCAL")
            {
                countryTracker.RecordDetection(
                    countryCode,
                    countryName ?? countryCode,
                    aggregated.BotProbability > 0.5,
                    aggregated.BotProbability);
            }
        }

        // Feed cluster service for bot detections
        if (clusterService != null && aggregated.BotProbability > 0.5)
            clusterService.NotifyBotDetected();
    }

    #endregion

    #region Response Headers

    private void AddResponseHeaders(
        HttpContext context,
        AggregatedEvidence aggregated,
        string policyName)
    {
        var headerConfig = _options.ResponseHeaders;
        var prefix = headerConfig.HeaderPrefix;

        // Always add policy name if configured
        if (headerConfig.IncludePolicyName) context.Response.Headers.TryAdd($"{prefix}Policy", policyName);

        // Risk score (always included)
        context.Response.Headers.TryAdd($"{prefix}Risk-Score", aggregated.BotProbability.ToString("F3"));
        context.Response.Headers.TryAdd($"{prefix}Risk-Band", aggregated.RiskBand.ToString());

        if (headerConfig.IncludeConfidence)
            context.Response.Headers.TryAdd($"{prefix}Confidence", aggregated.Confidence.ToString("F3"));

        if (headerConfig.IncludeDetectors && aggregated.ContributingDetectors.Count > 0)
            context.Response.Headers.TryAdd($"{prefix}Detectors",
                string.Join(",", aggregated.ContributingDetectors));

        if (headerConfig.IncludeProcessingTime)
            context.Response.Headers.TryAdd($"{prefix}Processing-Ms",
                aggregated.TotalProcessingTimeMs.ToString("F1"));

        if (aggregated.PolicyAction.HasValue)
            context.Response.Headers.TryAdd($"{prefix}Action", aggregated.PolicyAction.Value.ToString());

        if (aggregated.PrimaryBotName != null && headerConfig.IncludeBotName)
            context.Response.Headers.TryAdd($"{prefix}Bot-Name", aggregated.PrimaryBotName);

        if (aggregated.EarlyExit)
        {
            context.Response.Headers.TryAdd($"{prefix}Early-Exit", "true");
            if (aggregated.EarlyExitVerdict.HasValue)
                context.Response.Headers.TryAdd($"{prefix}Verdict", aggregated.EarlyExitVerdict.Value.ToString());
        }

        // Include AI status for calibration visibility. Hard-code the bool→string
        // constants instead of bool.ToString().ToLowerInvariant() — saves the
        // "True"/"False" alloc + the lowercased copy alloc per request.
        context.Response.Headers.TryAdd($"{prefix}Ai-Ran", aggregated.AiRan ? "true" : "false");

        // Threat scoring headers (from IntentContributor)
        if (headerConfig.IncludeThreatScore)
        {
            context.Response.Headers.TryAdd($"{prefix}Threat-Score", aggregated.ThreatScore.ToString("F3"));
            context.Response.Headers.TryAdd($"{prefix}Threat-Band", aggregated.ThreatBand.ToString());
        }

        // Full JSON result if enabled (useful for debugging)
        if (headerConfig.IncludeFullJson)
        {
            var jsonResult = new
            {
                risk = aggregated.BotProbability,
                confidence = aggregated.Confidence,
                riskBand = aggregated.RiskBand.ToString(),
                policy = policyName,
                aiRan = aggregated.AiRan,
                detectors = aggregated.ContributingDetectors,
                categories = aggregated.CategoryBreakdown.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Score)
            };
            var json = JsonSerializer.Serialize(jsonResult);
            // Base64 encode to avoid header encoding issues
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            context.Response.Headers.TryAdd($"{prefix}Result-Json", base64);
        }

        // Reason header: top contributing detector reason (opt-in per policy, PII-free).
        // Single-pass linear scan tracking the best (highest |delta * weight|) match -
        // previously a Where + OrderByDescending + FirstOrDefault chain that walked the
        // contribution list, sorted it, then took the first element. For 15-25 typical
        // contributions per request the sort is wasted work; we only need the maximum.
        if (headerConfig.IncludeReasonHeader && aggregated.Contributions.Count > 0)
        {
            string? topReason = null;
            double bestScore = 0;
            for (var i = 0; i < aggregated.Contributions.Count; i++)
            {
                var c = aggregated.Contributions[i];
                if (Math.Abs(c.ConfidenceDelta) <= 0.01) continue;
                var score = Math.Abs(c.ConfidenceDelta * c.Weight);
                if (score > bestScore)
                {
                    bestScore = score;
                    topReason = c.Reason;
                }
            }

            if (topReason is not null)
            {
                // Truncate to 200 chars, no PII (detector reasons are already PII-free)
                if (topReason.Length > 200) topReason = topReason[..200];
                context.Response.Headers.TryAdd($"{prefix}Reason", topReason);
            }
        }

        // Approval-Id header: for borderline requests that could be manually approved
        if (headerConfig.IncludeApprovalId
            && aggregated.BotProbability is >= 0.5 and <= 0.7
            && !aggregated.Signals.ContainsKey(SignalKeys.ApprovalVerified))
        {
            var approvalStore = context.RequestServices.GetService<Data.IFingerprintApprovalStore>();
            var signature = Orchestration.SignatureLookup.Primary(aggregated);
            if (approvalStore is not null && signature is not null)
            {
                try
                {
                    var token = approvalStore.GenerateApprovalTokenAsync(signature, context.RequestAborted)
                        .GetAwaiter().GetResult(); // Sync context - SQLite is local and fast
                    context.Response.Headers.TryAdd($"{prefix}Approval-Id", token);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Failed to generate approval token for {Signature}", signature);
                }
            }
        }
    }

    #endregion

    #region Detection Logging

    private void LogDetectionResult(
        HttpContext context,
        AggregatedEvidence aggregated,
        string policyName)
    {
        // Always log at Information level for visibility
        // LogAllRequests controls whether to log human traffic (low risk)
        // LogDetailedReasons controls whether to include detection reasons

        var riskScore = aggregated.BotProbability;
        var riskBand = aggregated.RiskBand;
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        var isLikelyBot = riskScore >= _options.BotThreshold;
#pragma warning restore CS0618

        // For low-risk (likely human) requests, only log if LogAllRequests is true
        if (!isLikelyBot && !_options.LogAllRequests) return;

        var path = context.Request.Path;
        var method = context.Request.Method;
        var botName = aggregated.PrimaryBotName ?? "unknown";

        if (_options.LogDetailedReasons && aggregated.Contributions.Count > 0)
        {
            // Detailed logging with reasons - build strings only in this branch
            var detectors = string.Join(",", aggregated.ContributingDetectors);
            var topReasons = aggregated.Contributions
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta))
                .Take(3)
                .Select(c => $"{c.Category}:{c.ConfidenceDelta:+0.00;-0.00}")
                .ToList();

            _logger.LogInformation(
                "Bot detection: {Method} {Path} -> risk={Risk:F2} ({RiskBand}), bot={BotName}, policy={Policy}, " +
                "detectors=[{Detectors}], reasons=[{Reasons}], time={Time:F1}ms",
                method, path, riskScore, riskBand, botName, policyName,
                detectors, string.Join(", ", topReasons), aggregated.TotalProcessingTimeMs);
        }
        else
        {
            _logger.LogInformation(
                "Bot detection: {Method} {Path} -> risk={Risk:F2} ({RiskBand}), bot={BotName}, policy={Policy}, time={Time:F1}ms",
                method, path, riskScore, riskBand, botName, policyName, aggregated.TotalProcessingTimeMs);
        }
    }

    #endregion

    #region HttpContext Item Keys

    /// <summary>Full BotDetectionResult object</summary>
    public const string BotDetectionResultKey = "BotDetectionResult";

    /// <summary>Full AggregatedEvidence from blackboard orchestrator</summary>
    public const string AggregatedEvidenceKey = "BotDetection.AggregatedEvidence";

    /// <summary>
    ///     HttpContext.Items key the middleware sets on test-mode dispatches so
    ///     the orchestrator can skip verdict-cache persistence for the
    ///     simulated UA. Without this, a single demo press writes the
    ///     simulated verdict to the cache and every subsequent real request
    ///     from the same fingerprint inherits it.
    /// </summary>
    public const string TestModeEphemeralKey = "BotDetection.TestMode.Ephemeral";

    /// <summary>
    ///     Legacy: previously held the precomputed primary signature on HttpContext.Items.
    ///     Read the signature from <see cref="AggregatedEvidence.Signals"/>[<see cref="SignalKeys.PrimarySignature"/>]
    ///     instead. Internal consumers no longer write this; the constant is retained so external
    ///     code that read it continues to compile until the next major. Will be removed at v8.
    ///     See <c>docs/architecture/signal-contracts.md</c>.
    /// </summary>
    [Obsolete("Read signature from AggregatedEvidence.Signals[SignalKeys.PrimarySignature] instead. Removed at v8.")]
    public const string PrimarySignatureKey = "BotDetection:Signature";

    /// <summary>
    ///     Legacy: previously held the full <see cref="Dashboard.MultiFactorSignatures"/> set on
    ///     HttpContext.Items. Read from <see cref="AggregatedEvidence.Signals"/>[<see cref="SignalKeys.SignatureMultifactor"/>]
    ///     instead. Will be removed at v8.
    /// </summary>
    [Obsolete("Read signature set from AggregatedEvidence.Signals[SignalKeys.SignatureMultifactor] instead. Removed at v8.")]
    public const string SignatureSetKey = "BotDetection.Signatures";

    /// <summary>Boolean: true if request is from a bot</summary>
    public const string IsBotKey = "BotDetection.IsBot";

    /// <summary>Double: bot probability score (0.0-1.0). Legacy name kept for backward compatibility.</summary>
    public const string BotConfidenceKey = "BotDetection.Confidence";

    /// <summary>Double: bot probability (0.0-1.0) - how likely the request is from a bot</summary>
    public const string BotProbabilityKey = "BotDetection.Probability";

    /// <summary>Double: detection confidence (0.0-1.0) - how certain the system is of its verdict</summary>
    public const string DetectionConfidenceKey = "BotDetection.DetectionConfidence";

    /// <summary>BotType?: the detected bot type</summary>
    public const string BotTypeKey = "BotDetection.BotType";

    /// <summary>String?: the detected bot name</summary>
    public const string BotNameKey = "BotDetection.BotName";

    /// <summary>String?: primary detection category (e.g., "UserAgent", "IP", "Header")</summary>
    public const string BotCategoryKey = "BotDetection.Category";

    /// <summary>List&lt;DetectionReason&gt;: all detection reasons</summary>
    public const string DetectionReasonsKey = "BotDetection.Reasons";

    /// <summary>String: name of the policy used for this request</summary>
    public const string PolicyNameKey = "BotDetection.PolicyName";

    /// <summary>PolicyAction?: action taken by policy (if any)</summary>
    public const string PolicyActionKey = "BotDetection.PolicyAction";

    #endregion

    #region Policy Resolution

    private DetectionPolicy ResolvePolicy(
        HttpContext context,
        Endpoint? endpoint,
        IPolicyRegistry policyRegistry)
    {
        // 0a. Check for policy query parameter (for demo/testing - only when test mode enabled)
        if (_options.EnableTestMode && context.Request.Query.TryGetValue("policy", out var policyParam))
        {
            var queryPolicy = policyRegistry.GetPolicy(policyParam.ToString());
            if (queryPolicy != null)
            {
                _logger.LogDebug("Using policy '{Policy}' from query parameter for {Path}",
                    queryPolicy.Name, context.Request.Path);
                return queryPolicy;
            }
        }

        // 0b. Check for X-Bot-Policy header for direct policy selection (test mode only)
        if (_options.EnableTestMode)
        {
            // X-Bot-Policy: <policyName> - direct policy selection
            if (context.Request.Headers.TryGetValue("X-Bot-Policy", out var policyHeader) &&
                !string.IsNullOrEmpty(policyHeader))
            {
                var headerPolicy = policyRegistry.GetPolicy(policyHeader!);
                if (headerPolicy != null)
                {
                    _logger.LogDebug("Using '{Policy}' policy from X-Bot-Policy header for {Path}",
                        headerPolicy.Name, context.Request.Path);
                    return headerPolicy;
                }
            }

            // Legacy headers for backwards compatibility
            if (context.Request.Headers.ContainsKey("X-Force-Slow-Path"))
            {
                var demoPolicy = policyRegistry.GetPolicy("demo");
                if (demoPolicy != null)
                {
                    _logger.LogDebug("Using 'demo' policy from X-Force-Slow-Path header for {Path}",
                        context.Request.Path);
                    return demoPolicy;
                }
            }
            else if (context.Request.Headers.ContainsKey("X-Force-Fast-Path"))
            {
                var fastPolicy = policyRegistry.GetPolicy("fastpath") ??
                                 policyRegistry.GetPolicy("default") ??
                                 policyRegistry.DefaultPolicy;
                _logger.LogDebug("Using '{Policy}' policy from X-Force-Fast-Path header for {Path}",
                    fastPolicy.Name, context.Request.Path);
                return fastPolicy;
            }
        }

        // 1. Check for [BotPolicy] attribute on action/controller
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();
        if (policyAttr != null && !string.IsNullOrEmpty(policyAttr.PolicyName))
        {
            var attrPolicy = policyRegistry.GetPolicy(policyAttr.PolicyName);
            if (attrPolicy != null)
            {
                _logger.LogDebug("Using policy '{Policy}' from attribute for {Path}",
                    attrPolicy.Name, context.Request.Path);
                return attrPolicy;
            }

            _logger.LogWarning("Policy '{Policy}' from attribute not found, falling back to path-based",
                policyAttr.PolicyName);
        }

        // 1b. Check for sandbox/probation policy override (set by sandbox action on previous request)
        if (context.Items.TryGetValue("BotDetection.SandboxPolicy", out var sandboxPolicyObj) &&
            sandboxPolicyObj is string sandboxPolicyName)
        {
            var sandboxPolicy = policyRegistry.GetPolicy(sandboxPolicyName);
            if (sandboxPolicy != null)
            {
                // If LLM sampling is disabled for this request, exclude the LLM detector
                if (context.Items.TryGetValue("BotDetection.SandboxUseLlm", out var useLlmObj) &&
                    useLlmObj is false)
                {
                    sandboxPolicy = sandboxPolicy with
                    {
                        ExcludedDetectors = sandboxPolicy.ExcludedDetectors.Add("Llm")
                    };
                }

                _logger.LogDebug("Using sandbox policy '{Policy}' for {Path} (probation mode)",
                    sandboxPolicy.Name, context.Request.Path);
                return sandboxPolicy;
            }
        }

        // 2. Fall back to path-based policy resolution. Routed endpoints
        //    carrying NotStaticAssetMarker metadata (e.g. MapStyloBotSitemap)
        //    bypass the file-extension static-asset shortcut so a dynamic
        //    /sitemap.xml or /feed.xml endpoint still runs full detection.
        DetectionPolicy resolvedPolicy;
        var routedEndpoint = context.GetEndpoint();
        var skipStaticShortcut = routedEndpoint?.Metadata?.GetMetadata<Extensions.NotStaticAssetMarker>() != null;
        if (skipStaticShortcut)
        {
            resolvedPolicy = policyRegistry.GetPolicy("default") ?? policyRegistry.GetPolicyForPath(context.Request.Path);
        }
        else
        {
            resolvedPolicy = policyRegistry.GetPolicyForPath(context.Request.Path);
        }

        // 3. Apply API key overlay if present
        if (context.Items.TryGetValue("BotDetection.ApiKeyContext", out var keyCtxObj) &&
            keyCtxObj is ApiKeyContext keyCtx)
        {
            // If key specifies a detection policy, use that instead
            if (!string.IsNullOrEmpty(keyCtx.DetectionPolicyName))
            {
                var keyPolicy = policyRegistry.GetPolicy(keyCtx.DetectionPolicyName);
                if (keyPolicy != null)
                    resolvedPolicy = keyPolicy;
            }

            // Apply detector exclusions and weight overrides
            resolvedPolicy = resolvedPolicy.WithApiKeyOverlay(keyCtx);
            _logger.LogDebug("Applied API key overlay '{KeyName}' to policy '{Policy}'",
                keyCtx.KeyName, resolvedPolicy.Name);
        }

        return resolvedPolicy;
    }

    private bool ShouldSkipPath(PathString path)
    {
        // Check ExcludedPaths first (complete bypass)
        foreach (var excludedPath in _options.ExcludedPaths)
            if (path.StartsWithSegments(excludedPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping bot detection for {Path} (ExcludedPaths)", path);
                return true;
            }

        // Also check ResponseHeaders.SkipPaths for backward compatibility
        foreach (var skipPath in _options.ResponseHeaders.SkipPaths)
            if (path.StartsWithSegments(skipPath, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    ///     Checks if the request carries a valid API bypass key.
    ///     Uses constant-time comparison to prevent timing attacks.
    /// </summary>
    private bool HasValidApiBypassKey(HttpContext context)
    {
        if (_options.ApiBypassKeys.Count == 0)
            return false;

        var headerName = _options.ApiBypassHeaderName;
        if (!context.Request.Headers.TryGetValue(headerName, out var headerValue) ||
            string.IsNullOrEmpty(headerValue.ToString()))
            return false;

        var providedKey = headerValue.ToString();
        var providedBytes = Encoding.UTF8.GetBytes(providedKey);

        foreach (var validKey in _options.ApiBypassKeys)
        {
            var validBytes = Encoding.UTF8.GetBytes(validKey);
            if (System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(providedBytes, validBytes))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Resolve the per-bot-type action policy name for a detected bot, applying
    ///     the internal-network bucket first when the request satisfies BOTH
    ///     <c>ip.is_local=true</c> AND has a valid API key context. Either signal
    ///     alone falls through to <see cref="BotDetectionOptions.BotTypeActionPolicies"/>.
    ///     Returns null when no matching entry exists in either map (caller falls
    ///     through to <see cref="BotDetectionOptions.DefaultActionPolicyName"/>).
    /// </summary>
    private string? ResolveBotTypeActionPolicy(
        AggregatedEvidence evidence,
        ApiKeyContext? apiKeyContext)
    {
        if (evidence.PrimaryBotType is null || evidence.PrimaryBotType == BotType.Unknown)
            return null;

        var botTypeName = evidence.PrimaryBotType.Value.ToString();

        if (apiKeyContext is not null
            && _options.InternalNetworkBotTypeActionPolicies.Count > 0
            && evidence.Signals.TryGetValue(SignalKeys.IpIsLocal, out var ipLocalObj)
            && ipLocalObj is bool isLocal
            && isLocal
            && _options.InternalNetworkBotTypeActionPolicies.TryGetValue(botTypeName, out var internalPolicy)
            && !string.IsNullOrEmpty(internalPolicy))
        {
            return internalPolicy;
        }

        if (_options.BotTypeActionPolicies.Count > 0
            && _options.BotTypeActionPolicies.TryGetValue(botTypeName, out var normalPolicy)
            && !string.IsNullOrEmpty(normalPolicy))
        {
            return normalPolicy;
        }

        return null;
    }

    /// <summary>
    ///     Tries to validate a rich API key from the request header.
    ///     Returns the ApiKeyContext if valid, or null if no rich key matched.
    ///     Stores rejection reason in HttpContext.Items if key was found but rejected.
    /// </summary>
    private ApiKeyContext? TryValidateRichApiKey(HttpContext context, IApiKeyStore? apiKeyStore)
    {
        if (apiKeyStore == null || _options.ApiKeys.Count == 0)
            return null;

        var headerName = _options.ApiBypassHeaderName;
        if (!context.Request.Headers.TryGetValue(headerName, out var headerValue) ||
            string.IsNullOrEmpty(headerValue.ToString()))
            return null;

        var providedKey = headerValue.ToString();
        var (result, rejection) = apiKeyStore.ValidateKeyWithReason(providedKey, context.Request.Path);

        if (result != null)
            return result.Context;

        if (rejection != null &&
            (_options.RejectUnknownApiKeys || rejection.Reason != ApiKeyRejectionReason.NotFound))
        {
            // Key was rejected (or unknown keys are configured to reject) - store rejection for the caller
            context.Items["BotDetection.ApiKeyRejection"] = rejection;
        }

        return null;
    }

    private bool IsSignatureOnlyPath(PathString path)
    {
        foreach (var sigPath in _options.SignatureOnlyPaths)
            if (path.StartsWithSegments(sigPath, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    ///     Compute the request's primary signature for pre-detection consumers (verdict gate,
    ///     signature-only paths). Post-detection consumers must read from
    ///     <see cref="AggregatedEvidence.Signals"/>[<see cref="SignalKeys.PrimarySignature"/>] —
    ///     the foundation wave's <see cref="SignatureContributor"/> populates it. Returns null
    ///     if the signature service is not registered.
    /// </summary>
    private string? ComputeSignature(HttpContext context)
    {
        if (_signatureService == null) return null;
        return _signatureService.GenerateSignatures(context).PrimarySignature;
    }

    /// <summary>
    ///     Checks if the path has an override that allows it through regardless of detection.
    /// </summary>
    private bool HasPathOverride(PathString path, out string? overrideAction)
    {
        overrideAction = null;

        foreach (var (pattern, action) in _options.PathOverrides)
            if (MatchesPathPattern(path, pattern))
            {
                overrideAction = action;
                return true;
            }

        return false;
    }

    /// <summary>
    ///     Matches a path against a pattern with glob support.
    ///     Supports: exact match, prefix with *, and ** for recursive matching.
    /// </summary>
    private static bool MatchesPathPattern(PathString path, string pattern)
    {
        var pathValue = path.Value ?? "";

        // Exact match
        if (pattern.Equals(pathValue, StringComparison.OrdinalIgnoreCase))
            return true;

        // Prefix match with single * (e.g., "/api/public/*" matches "/api/public/foo" but not "/api/public/foo/bar")
        if (pattern.EndsWith("/*"))
        {
            var prefix = pattern[..^2];
            if (pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var remainder = pathValue[prefix.Length..];
                // Must have exactly one more segment (starts with / and no more /)
                return remainder.StartsWith('/') && !remainder[1..].Contains('/');
            }
        }

        // Recursive match with ** (e.g., "/api/public/**" matches any depth)
        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern[..^3];
            return pathValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Simple prefix match (e.g., "/api/public" matches "/api/public" and "/api/public/anything")
        if (!pattern.Contains('*')) return pathValue.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    #endregion

    #region Blocking Logic

    private (bool Block, BotBlockAction Action) ShouldBlockRequest(
        AggregatedEvidence aggregated,
        DetectionPolicy policy,
        BotPolicyAttribute? policyAttr)
    {
        // Check attribute skip
        if (policyAttr?.Skip == true)
            return (false, BotBlockAction.Default);

        // Determine action from attribute or default
        var defaultAction = policyAttr?.BlockAction ?? BotBlockAction.Default;

        // Confidence gate: attribute override > policy default > 0 (disabled)
        var minConfidence = policyAttr?.MinConfidence is > 0 ? policyAttr.MinConfidence : policy.MinConfidence;

        // If confidence gate is set and not met, don't block (insufficient evidence)
        if (minConfidence > 0 && aggregated.Confidence < minConfidence)
        {
            _logger.LogDebug(
                "Confidence gate not met for {Path}: confidence={Confidence:F2} < required={Required:F2}, skipping block",
                "", aggregated.Confidence, minConfidence);
            return (false, BotBlockAction.Default);
        }

        // Check if policy action says to block
        if (aggregated.PolicyAction == DetectionPolicyAction.Block)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Check if policy action says to throttle
        if (aggregated.PolicyAction == DetectionPolicyAction.Throttle)
            return (true, BotBlockAction.Throttle);

        // Check if policy action says to challenge
        if (aggregated.PolicyAction == DetectionPolicyAction.Challenge)
            return (true, BotBlockAction.Challenge);

        // Check for verified bad bot (bypasses confidence gate - verified bots are always high confidence)
        if (aggregated.EarlyExit && aggregated.EarlyExitVerdict == EarlyExitVerdict.VerifiedBadBot)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Never block verified good bots (Googlebot, Bingbot, etc.) at the middleware level.
        // Endpoint-level filters ([BlockBots], .BlockBots()) decide whether to allow them.
        if (aggregated.EarlyExit && aggregated.EarlyExitVerdict == EarlyExitVerdict.VerifiedGoodBot)
            return (false, BotBlockAction.Default);

        // Check if risk exceeds immediate block threshold
        if (aggregated.BotProbability >= policy.ImmediateBlockThreshold)
            return (true, defaultAction == BotBlockAction.Default ? BotBlockAction.StatusCode : defaultAction);

        // Global BlockDetectedBots: block all detected bots app-wide (respecting allow-lists)
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        if (_options.BlockDetectedBots
            && aggregated.BotProbability >= _options.MinConfidenceToBlock)
        {
            // Allow through bot types configured in global options
            if (BotTypeFilter.IsBotTypeAllowed(aggregated.PrimaryBotType,
                    allowSearchEngines: _options.AllowVerifiedSearchEngines,
                    allowSocialMediaBots: _options.AllowSocialMediaBots,
                    allowMonitoringBots: _options.AllowMonitoringBots,
                    allowVerifiedBots: _options.AllowVerifiedSearchEngines,
                    allowTools: _options.AllowTools))
                return (false, BotBlockAction.Default);

            return (true, BotBlockAction.StatusCode);
        }
#pragma warning restore CS0618

        return (false, BotBlockAction.Default);
    }

    private async Task HandleBlockedRequest(
        HttpContext context,
        AggregatedEvidence aggregated,
        DetectionPolicy policy,
        BotPolicyAttribute? policyAttr,
        BotBlockAction action)
    {
        // --- Holodeck engagement check ---
        // If path was tagged as honeypot (pre-detection) or attack signals fired,
        // try holodeck action policy instead of hard block.
        var isHoneypotPath = context.Items.TryGetValue("Holodeck.IsHoneypotPath", out var hpVal) && hpVal is true;
        var hasAttackSignal = aggregated.Signals.ContainsKey(SignalKeys.AttackDetected);

        if (isHoneypotPath || hasAttackSignal)
        {
            // Set fingerprint for downstream action policies (SimulationPackResponder uses this)
            if (Orchestration.SignatureLookup.Primary(aggregated) is { } fp)
                context.Items["Holodeck.Fingerprint"] = fp;

            var holodeckRegistry = context.RequestServices.GetService<IActionPolicyRegistry>();
            var holodeckPolicy = holodeckRegistry?.GetPolicy("holodeck");
            if (holodeckPolicy != null)
            {
                _logger.LogInformation(
                    "[HOLODECK] Engaging for {Path} (honeypot={IsHoneypot}, attack={HasAttack})",
                    context.Request.Path, isHoneypotPath, hasAttackSignal);

                var holoResult = await holodeckPolicy.ExecuteAsync(context, aggregated, context.RequestAborted);
                if (!holoResult.Continue) return;
                // If holodeck returned Continue (e.g., coordinator busy), fall through to normal block
            }
        }
        // --- End holodeck check ---

        var riskScore = aggregated.BotProbability;

        _logger.LogWarning(
            "[BLOCK] Request blocked: {Path} policy={Policy} risk={Risk:F2} action={Action}",
            context.Request.Path, policy.Name, riskScore, action);

        switch (action)
        {
            case BotBlockAction.StatusCode:
            case BotBlockAction.Default:
                var statusCode = policyAttr?.BlockStatusCode ?? 403;
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new BlockedResponse(
                    "Access denied",
                    "Request blocked by bot detection",
                    riskScore,
                    policy.Name
                ));
                break;

            case BotBlockAction.Redirect:
                var redirectUrl = policyAttr?.BlockRedirectUrl ?? _options.Throttling.BlockRedirectUrl ?? "/blocked";
                context.Response.Redirect(redirectUrl);
                break;

            case BotBlockAction.Challenge:
                context.Response.StatusCode = 403;
                context.Response.Headers["X-Bot-Challenge"] = "required";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new ChallengeResponse(
                    "Challenge required",
                    _options.Throttling.ChallengeType,
                    riskScore
                ));
                break;

            case BotBlockAction.Throttle:
                await HandleThrottle(context, riskScore);
                break;

            case BotBlockAction.LogOnly:
                // Log but don't block - continue to next middleware
                _logger.LogWarning(
                    "Bot detected (shadow mode): path={Path}, risk={Risk:F2}, policy={Policy}",
                    context.Request.Path, riskScore, policy.Name);
                EnsureMaliciousFallbackMaskPii(context);
                await InvokeNextWithResponseMutationAsync(context);
                ApplyResponseStatusBoost(context);
                break;
        }
    }

    private async Task HandleThrottle(HttpContext context, double riskScore)
    {
        var throttleConfig = _options.Throttling;

        // Calculate retry delay with optional jitter
        var baseDelay = throttleConfig.BaseDelaySeconds;
        var delay = baseDelay;

        if (throttleConfig.EnableJitter)
        {
            // Add jitter: ±JitterPercent of base delay
            var jitterRange = baseDelay * (throttleConfig.JitterPercent / 100.0);
            var jitterValue = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
            delay = Math.Max(1, baseDelay + (int)jitterValue);
        }

        // Scale delay by risk score if configured
        if (throttleConfig.ScaleByRisk) delay = (int)(delay * (1 + riskScore));

        // Cap at max delay
        delay = Math.Min(delay, throttleConfig.MaxDelaySeconds);

        context.Response.StatusCode = 429;
        context.Response.Headers["Retry-After"] = delay.ToString();

        // Add jitter indication header (helps with debugging, doesn't reveal exact algorithm)
        if (throttleConfig.EnableJitter) context.Response.Headers["X-Retry-Jitter"] = "applied";

        context.Response.ContentType = "application/json";

        // Optionally delay response to slow down bots
        if (throttleConfig.DelayResponse)
        {
            var responseDelay = Math.Min(throttleConfig.ResponseDelayMs, 5000);
            if (throttleConfig.EnableJitter)
            {
                // Add jitter to response delay too
                var delayJitter = (int)(responseDelay * (throttleConfig.JitterPercent / 100.0));
                responseDelay += Random.Shared.Next(-delayJitter, delayJitter);
                responseDelay = Math.Max(100, responseDelay);
            }

            await Task.Delay(responseDelay, context.RequestAborted);
        }

        // AOT-safe: no anonymous types with WriteAsJsonAsync
        var msg = throttleConfig.ThrottleMessage?.Replace("\"", "\\\"") ?? "";
        await context.Response.WriteAsync(
            $$"""{"error":"Too many requests","retryAfter":{{delay}},"message":"{{msg}}"}""");
    }

    #endregion

    #region Test Mode

    private async Task HandleTestModeWithRealDetection(
        HttpContext context,
        string testMode,
        IDetectionOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator,
        AuditProcessorDispatcher? auditProcessorDispatcher)
    {
        _logger.LogInformation("Test mode: Running real detection with simulated UA for '{Mode}'", testMode);

        // Handle "disable" mode - skip detection entirely
        if (testMode.Equals("disable", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.TryAdd("X-Test-Mode", "disabled");
            await _next(context);
            return;
        }

        // Get simulated User-Agent from options first, then fallback to defaults
        string? simulatedUserAgent = null;
        if (_options.TestModeSimulations.TryGetValue(testMode, out var configuredUa))
            simulatedUserAgent = configuredUa;
        else if (DefaultTestModeSimulations.TryGetValue(testMode, out var defaultUa))
            simulatedUserAgent = defaultUa;

        await RunDetectionWithOverriddenUaAsync(
            context,
            simulatedUserAgent ?? context.Request.Headers.UserAgent.ToString(),
            orchestrator,
            policyRegistry,
            actionPolicyRegistry,
            responseCoordinator,
            auditProcessorDispatcher,
            testModeHeader: "true",
            uaHeader: ("X-Test-Simulated-UA", simulatedUserAgent ?? "none"),
            logPrefix: "Test mode");
    }

    /// <summary>
    ///     Handles custom User-Agent testing via ml-bot-test-ua header.
    ///     Allows testing with any arbitrary User-Agent string.
    /// </summary>
    private async Task HandleCustomUaDetection(
        HttpContext context,
        string customUa,
        IDetectionOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator,
        AuditProcessorDispatcher? auditProcessorDispatcher)
    {
        _logger.LogInformation("Custom UA test: Running real detection with '{UA}'", customUa);

        await RunDetectionWithOverriddenUaAsync(
            context,
            customUa,
            orchestrator,
            policyRegistry,
            actionPolicyRegistry,
            responseCoordinator,
            auditProcessorDispatcher,
            testModeHeader: "custom-ua",
            uaHeader: ("X-Test-Custom-UA", customUa),
            logPrefix: "Custom UA test");
    }

    /// <summary>
    ///     Common method for running detection with an overridden User-Agent.
    ///     Handles UA override, detection, action policies, and blocking.
    /// </summary>
    private async Task RunDetectionWithOverriddenUaAsync(
        HttpContext context,
        string overrideUserAgent,
        IDetectionOrchestrator orchestrator,
        IPolicyRegistry policyRegistry,
        IActionPolicyRegistry actionPolicyRegistry,
        ResponseCoordinator responseCoordinator,
        AuditProcessorDispatcher? auditProcessorDispatcher,
        string testModeHeader,
        (string Name, string Value) uaHeader,
        string logPrefix)
    {
        var originalUserAgent = context.Request.Headers.UserAgent.ToString();
        context.Request.Headers.UserAgent = overrideUserAgent;

        // Test mode is ephemeral: the simulated verdict must NOT be persisted to
        // the verdict cache, otherwise a single `?demo=googlebot` press poisons
        // every subsequent request from the same fingerprint with the bot
        // verdict (the cache is keyed on the IP-plus-stable-headers signature,
        // which the UA override leaves unchanged). Orchestrators check this key
        // before calling `_fingerprintStore.RecordVerdictAsync`.
        context.Items[TestModeEphemeralKey] = true;

        AggregatedEvidence? aggregatedResult = null;
        DetectionPolicy? policy = null;

        try
        {
            var endpoint = context.GetEndpoint();
            policy = ResolvePolicy(context, endpoint, policyRegistry);

            // Load-shed gate: at High/Critical load, skip detection per policy.LoadShed.
            // Uses connection-id-hash as the seed so retries from the same client land
            // identically. Sheds emit X-StyloBot-Shed=1 for observability.
            // The finally block below restores the original User-Agent header.
            if (_loadShedDecision is not null)
            {
                var loadShedSeed = context.Connection?.Id?.GetHashCode()
                    ?? context.Request.Path.Value?.GetHashCode()
                    ?? 0;
                if (_loadShedDecision.ShouldShed(policy.LoadShed, loadShedSeed))
                {
                    context.Response.Headers["X-StyloBot-Shed"] = "1";
                    _logger.LogInformation("Load-shed: skipping detection for {Path}", context.Request.Path);
                    await _next(context);
                    return;
                }
            }

            // Wrapped in try/catch so policy.OnFailure governs the response when the
            // pipeline throws (detector bug, store unavailable, etc.) instead of the
            // request crashing into a generic HTTP 500.
            try
            {
                aggregatedResult = await orchestrator.DetectWithPolicyAsync(context, policy, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client aborted; rethrow so ASP.NET handles connection teardown normally.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Detection pipeline failed for {Path}; applying policy.OnFailure={Mode}",
                    context.Request.Path, policy.OnFailure);

                var fh = HandleDetectionFailureFor(policy);
                var fr = fh.Apply(context, ex);
                if (!fr.ContinuePipeline)
                    return;

                // Synthesise a neutral verdict so downstream middleware sees a consistent shape.
                aggregatedResult = new AggregatedEvidence
                {
                    BotProbability = 0.0,
                    Confidence = 0.0,
                    RiskBand = RiskBand.Low,
                    ThreatBand = ThreatBand.Low,
                    TotalProcessingTimeMs = 0,
                };
            }

            PopulateContextFromAggregated(context, aggregatedResult, policy.Name);
            FeedDetectionServices(context, aggregatedResult);

            context.Response.Headers.TryAdd("X-Test-Mode", testModeHeader);
            context.Response.Headers.TryAdd(uaHeader.Name, uaHeader.Value);

            if (_options.ResponseHeaders.Enabled)
                AddResponseHeaders(context, aggregatedResult, policy.Name);

            _logger.LogInformation(
                "{LogPrefix} result: BotProbability={Probability:P0}, Detectors={Count}, Processing={Ms:F1}ms, ActionPolicy={ActionPolicy}",
                logPrefix,
                aggregatedResult.BotProbability,
                aggregatedResult.ContributingDetectors.Count,
                aggregatedResult.TotalProcessingTimeMs,
                aggregatedResult.TriggeredActionPolicyName ?? "none");
        }
        finally
        {
            context.Request.Headers.UserAgent = originalUserAgent;
        }

        // Register response recording BEFORE any early exits (blocked, action policy, etc.)
        if (aggregatedResult != null)
        {
            var testStartTime = DateTime.UtcNow;
            var testReq = CaptureRequestSnapshot(context);
            var testBotProbability = aggregatedResult.BotProbability;
            var testAuditCtx = auditProcessorDispatcher?.HasProcessors == true
                ? auditProcessorDispatcher.BuildContext(context, aggregatedResult)
                : null;

            context.Response.OnCompleted(() =>
            {
                var finalEvidence = context.Items[AggregatedEvidenceKey] as AggregatedEvidence ?? aggregatedResult;
                var finalAction = finalEvidence.PolicyAction;

                var statusCode = context.Response.StatusCode;
                var contentLength = context.Response.ContentLength ?? 0;
                var contentType = context.Response.ContentType;
                var processingTimeMs = (DateTime.UtcNow - testStartTime).TotalMilliseconds;

                var signal = BuildResponseSignal(
                    testReq.ClientId, testReq.RequestId, testReq.Path, testReq.Method,
                    statusCode, contentLength, contentType,
                    processingTimeMs, testBotProbability, finalAction);

                if (signal is not null)
                    _ = responseCoordinator.RecordResponseAsync(signal, CancellationToken.None);

                if (testAuditCtx is not null)
                {
                    var finalAuditCtx = testAuditCtx with
                    {
                        Metadata = testAuditCtx.Metadata with { StatusCode = statusCode }
                    };
                    _ = auditProcessorDispatcher!.DispatchPrebuiltAsync(finalAuditCtx, CancellationToken.None);
                }

                return Task.CompletedTask;
            });
        }

        // Handle post-detection actions (action policies and blocking)
        if (await HandlePostDetectionActionsAsync(context, aggregatedResult, policy, actionPolicyRegistry, logPrefix))
            return;

        EnsureMaliciousFallbackMaskPii(context);
        await InvokeNextWithResponseMutationAsync(context);
        ApplyResponseStatusBoost(context);
    }

    /// <summary>
    ///     ObserveOnly hook: when <see cref="BotDetectionOptions.ObserveOnly"/>
    ///     is set, swap the resolved action policy for <c>logonly</c> so the
    ///     visitor sees no behaviour change while the dashboard still records
    ///     which policy would have fired (via
    ///     <see cref="AggregatedEvidence.TriggeredActionPolicyName"/>).
    ///     The substitution is a no-op when the original policy already
    ///     carries <see cref="PolicyIntent.Pass"/>.
    /// </summary>
    /// <param name="registry">Action policy registry (used to fetch <c>logonly</c>).</param>
    /// <param name="resolved">The policy that would normally execute.</param>
    /// <returns>
    ///     <paramref name="resolved"/> when observe-only is off, otherwise
    ///     <c>logonly</c> from the registry. Falls back to
    ///     <paramref name="resolved"/> if <c>logonly</c> isn't registered.
    /// </returns>
    private IActionPolicy? MaybeShadowForObserveOnly(IActionPolicyRegistry registry, IActionPolicy? resolved)
    {
        if (!_options.ObserveOnly) return resolved;
        if (resolved is null) return null;
        if (resolved.Intent == PolicyIntent.Pass) return resolved;
        var shadow = registry.GetPolicy("logonly");
        if (shadow is not null) return shadow;
        // logonly is a built-in -- if it's missing the operator has gone out
        // of their way to deregister it. Warn loudly: applying the original
        // policy in observe-only mode is the OPPOSITE of what the operator
        // asked for, but failing open would let bots through entirely.
        _logger.LogWarning(
            "ObserveOnly is set but the 'logonly' policy is not registered. " +
            "Applying the configured policy '{Policy}' instead -- observe-only is NOT effective. " +
            "Re-register the logonly built-in or unset ObserveOnly.",
            resolved.Name);
        return resolved;
    }

    /// <summary>
    ///     Handles action policies and blocking logic after detection.
    ///     Returns true if request was handled (blocked/action executed), false to continue pipeline.
    /// </summary>
    private async Task<bool> HandlePostDetectionActionsAsync(
        HttpContext context,
        AggregatedEvidence? aggregatedResult,
        DetectionPolicy? policy,
        IActionPolicyRegistry actionPolicyRegistry,
        string logPrefix)
    {
        if (aggregatedResult == null || policy == null)
            return false;

        // Execute action policy if triggered
        if (!string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName))
        {
            var actionPolicy = MaybeShadowForObserveOnly(
                actionPolicyRegistry,
                actionPolicyRegistry.GetPolicy(aggregatedResult.TriggeredActionPolicyName));
            if (actionPolicy != null)
            {
                _logger.LogInformation(
                    "[ACTION] {LogPrefix} executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2})",
                    logPrefix, aggregatedResult.TriggeredActionPolicyName,
                    _options.ObserveOnly ? " [observe-only shadow]" : "",
                    context.Request.Path, aggregatedResult.BotProbability);

                var actionResult = await actionPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
                if (!actionResult.Continue)
                    return true;

                // Action policy allows continuation (e.g., logonly) - the policy IS the
                // response strategy, so skip ShouldBlockRequest below.
                return false;
            }
            else
            {
                _logger.LogWarning(
                    "Action policy '{ActionPolicy}' not found in registry ({LogPrefix})",
                    aggregatedResult.TriggeredActionPolicyName, logPrefix);
            }
        }

        // Fallback: per-bot-type action policy, then DefaultActionPolicyName.
        // When this fires, it replaces the hard block - the policy IS the response.
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        if (string.IsNullOrEmpty(aggregatedResult.TriggeredActionPolicyName)
            && aggregatedResult.BotProbability >= _options.BotThreshold
            && aggregatedResult.EarlyExitVerdict is not (EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted))
#pragma warning restore CS0618
        {
            // Try bot-type-specific policy first (internal-network bucket
            // wins when both ip.is_local AND a valid API key are present).
            // apiKeyContext is stashed on Items by InvokeAsync after validation;
            // this method runs further down the same pipeline so the cast is safe.
            var apiKeyCtx = context.Items.TryGetValue("BotDetection.ApiKeyContext", out var ctxObj)
                ? ctxObj as ApiKeyContext
                : null;
            var resolvedPolicyName = ResolveBotTypeActionPolicy(aggregatedResult, apiKeyCtx);

            // Fall back to default
            resolvedPolicyName ??= _options.DefaultActionPolicyName;

            if (!string.IsNullOrEmpty(resolvedPolicyName))
            {
                var fallbackPolicy = MaybeShadowForObserveOnly(
                    actionPolicyRegistry,
                    actionPolicyRegistry.GetPolicy(resolvedPolicyName));
                if (fallbackPolicy != null)
                {
                    aggregatedResult = aggregatedResult with
                    {
                        TriggeredActionPolicyName = resolvedPolicyName
                    };
                    context.Items[AggregatedEvidenceKey] = aggregatedResult;

                    _logger.LogInformation(
                        "[ACTION] {LogPrefix} executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2}, type={BotType})",
                        logPrefix, resolvedPolicyName,
                        _options.ObserveOnly ? " [observe-only shadow]" : "",
                        context.Request.Path, aggregatedResult.BotProbability, aggregatedResult.PrimaryBotType);

                    var fallbackResult = await fallbackPolicy.ExecuteAsync(context, aggregatedResult, context.RequestAborted);
                    return !fallbackResult.Continue;
                }
            }
        }

        // Check for blocking
        var endpoint = context.GetEndpoint();
        var policyAttr = endpoint?.Metadata.GetMetadata<BotPolicyAttribute>();
        var shouldBlock = ShouldBlockRequest(aggregatedResult, policy, policyAttr);

        if (shouldBlock.Block)
        {
            await HandleBlockedRequest(context, aggregatedResult, policy, policyAttr, shouldBlock.Action);
            return true;
        }

        return false;
    }

    private static BotDetectionResult CreateTestResult(string testMode)
    {
        // Simple fallback for legacy mode - just creates a generic result
        var isHuman = testMode.Equals("human", StringComparison.OrdinalIgnoreCase);
        return new BotDetectionResult
        {
            IsBot = !isHuman,
            ConfidenceScore = isHuman ? 0.0 : 0.7,
            BotType = isHuman ? null : BotType.Unknown,
            BotName = isHuman ? null : $"Test {testMode}",
            Reasons =
            [
                new DetectionReason
                {
                    Category = "Test Mode",
                    Detail = $"Simulated '{testMode}' (legacy fallback)",
                    ConfidenceImpact = isHuman ? 0.0 : 0.7
                }
            ]
        };
    }

    #endregion

    #region Upstream Trust

    /// <summary>
    ///     Attempts to hydrate HttpContext.Items from upstream gateway detection headers.
    ///     Returns true if upstream headers were found and context was populated, false to fall through.
    /// </summary>
    private bool TryHydrateFromUpstream(HttpContext context)
    {
        // Gateway must have sent X-Bot-Detected header
        if (!context.Request.Headers.TryGetValue("X-Bot-Detected", out var detectedHeader))
            return false;

        // Verify HMAC signature if configured
        if (!string.IsNullOrEmpty(_options.UpstreamSignatureHeader) &&
            !string.IsNullOrEmpty(_options.UpstreamSignatureSecret))
        {
            if (!context.Request.Headers.TryGetValue(_options.UpstreamSignatureHeader, out var signatureHeader) ||
                string.IsNullOrEmpty(signatureHeader.ToString()))
            {
                _logger.LogWarning("Upstream detection headers present but missing signature header '{Header}' - rejecting",
                    _options.UpstreamSignatureHeader);
                return false;
            }

            // Signature = HMACSHA256(X-Bot-Detected + ":" + X-Bot-Confidence + ":" + timestamp, secret)
            var timestamp = context.Request.Headers["X-Bot-Detection-Timestamp"].FirstOrDefault() ?? "";

            // Reject missing or invalid timestamps - replay protection requires a valid timestamp
            if (!long.TryParse(timestamp, out var epochSeconds))
            {
                _logger.LogWarning("Upstream detection missing or invalid timestamp - rejecting (replay protection)");
                return false;
            }

            var signedAt = DateTimeOffset.FromUnixTimeSeconds(epochSeconds);
            var age = DateTimeOffset.UtcNow - signedAt;
            if (age.Duration() > TimeSpan.FromSeconds(_options.UpstreamSignatureMaxAgeSeconds))
            {
                _logger.LogWarning("Upstream detection signature expired (age: {Age}) - rejecting", age);
                return false;
            }

            var payload = $"{detectedHeader}:{context.Request.Headers["X-Bot-Confidence"].FirstOrDefault()}:{timestamp}";

            try
            {
                var secretBytes = Convert.FromBase64String(_options.UpstreamSignatureSecret);
                using var hmac = new System.Security.Cryptography.HMACSHA256(secretBytes);
                var expectedBytes = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));

                byte[] actualBytes;
                try { actualBytes = Convert.FromBase64String(signatureHeader.ToString()); }
                catch { actualBytes = []; }

                // Constant-time comparison to prevent timing attacks
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
                {
                    _logger.LogWarning("Upstream detection signature mismatch - possible spoofing attempt");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to verify upstream detection signature");
                return false;
            }
        }

        var isBot = string.Equals(detectedHeader.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        // Parse bot probability and confidence from headers
        // Prefer explicit X-Bot-Detection-Probability header, fall back to X-Bot-Confidence for backward compatibility
        var botProbability = 0.0;
        var confidence = 0.0;

        // Try explicit probability header first
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-Probability", out var probHeader) &&
            double.TryParse(probHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedProb))
        {
            botProbability = Math.Clamp(parsedProb, 0.0, 1.0);
        }
        // Try confidence header as fallback for bot probability (backward compat)
        else if (context.Request.Headers.TryGetValue("X-Bot-Confidence", out var confHeader) &&
                 double.TryParse(confHeader.ToString(), System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out var parsedConf))
        {
            botProbability = Math.Clamp(parsedConf, 0.0, 1.0);
        }
        else
        {
            // Neither header present
            return false;
        }

        // Parse actual confidence if available (distinct from bot probability)
        confidence = botProbability; // Default: confidence = probability
        if (context.Request.Headers.TryGetValue("X-Bot-Confidence", out var actualConfHeader) &&
            double.TryParse(actualConfHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var actualConf) &&
            context.Request.Headers.ContainsKey("X-Bot-Detection-Probability")) // Only use confidence header if probability header also exists
        {
            confidence = Math.Clamp(actualConf, 0.0, 1.0);
        }

        // Parse optional fields
        BotType? botType = null;
        if (context.Request.Headers.TryGetValue("X-Bot-Type", out var typeHeader) &&
            Enum.TryParse<BotType>(typeHeader.ToString(), true, out var parsedType))
            botType = parsedType;

        var botName = context.Request.Headers["X-Bot-Name"].FirstOrDefault();
        var category = context.Request.Headers["X-Bot-Category"].FirstOrDefault();

        // Parse risk band
        var riskBand = RiskBand.Unknown;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-RiskBand", out var riskHeader) &&
            Enum.TryParse<RiskBand>(riskHeader.ToString(), true, out var parsedRisk))
            riskBand = parsedRisk;
        else
            riskBand = botProbability switch
            {
                >= 0.85 => RiskBand.VeryHigh,
                >= 0.7 => RiskBand.High,
                >= 0.5 => RiskBand.Medium,
                >= 0.3 => RiskBand.Elevated,
                >= 0.15 => RiskBand.Low,
                _ => RiskBand.VeryLow
            };

        // Parse processing time
        double processingMs = 0;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-ProcessingMs", out var procHeader))
            double.TryParse(procHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out processingMs);

        // Parse action
        var actionName = context.Request.Headers["X-Bot-Detection-Action"].FirstOrDefault();

        // Parse contributions from gateway (JSON array)
        // This populates the detector breakdown, reasons, and processing metrics
        DetectionLedger? ledger = null;
        var contributingDetectorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var detectionReasons = new List<DetectionReason>();

        if (context.Request.Headers.TryGetValue("X-Bot-Detection-Contributions", out var contribHeader) &&
            !string.IsNullOrEmpty(contribHeader.ToString()))
        {
            try
            {
                var contribs = JsonSerializer.Deserialize<List<UpstreamContribution>>(contribHeader.ToString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (contribs is { Count: > 0 })
                {
                    ledger = new DetectionLedger(context.TraceIdentifier);
                    foreach (var c in contribs)
                    {
                        var contribution = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
                        {
                            DetectorName = c.Name ?? "Unknown",
                            Category = c.Category ?? "Unknown",
                            ConfidenceDelta = c.ConfidenceDelta,
                            Weight = c.Weight,
                            Reason = c.Reason ?? "",
                            ProcessingTimeMs = c.ExecutionTimeMs,
                            Priority = c.Priority
                        };
                        ledger.AddContribution(contribution);
                        contributingDetectorNames.Add(c.Name ?? "Unknown");

                        if (!string.IsNullOrEmpty(c.Reason))
                        {
                            detectionReasons.Add(new DetectionReason
                            {
                                Category = c.Category ?? "Unknown",
                                Detail = c.Reason,
                                ConfidenceImpact = c.ConfidenceDelta
                            });
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream contributions JSON");
            }
        }

        // Fall back to reasons header if no contributions
        if (detectionReasons.Count == 0 &&
            context.Request.Headers.TryGetValue("X-Bot-Detection-Reasons", out var reasonsHeader) &&
            !string.IsNullOrEmpty(reasonsHeader.ToString()))
        {
            try
            {
                var reasons = JsonSerializer.Deserialize<List<string>>(reasonsHeader.ToString());
                if (reasons != null)
                {
                    detectionReasons.AddRange(reasons.Select(r => new DetectionReason
                    {
                        Category = "upstream",
                        Detail = r,
                        ConfidenceImpact = 0
                    }));
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream reasons JSON");
            }
        }

        // Parse forwarded signals from upstream gateway (X-Bot-Detection-Signals JSON header)
        IReadOnlyDictionary<string, object> upstreamSignals = new Dictionary<string, object>();
        var signalsHeader = context.Request.Headers["X-Bot-Detection-Signals"].FirstOrDefault();
        if (!string.IsNullOrEmpty(signalsHeader) && signalsHeader.Length <= 16_384)
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(signalsHeader);
                if (parsed != null)
                {
                    var dict = new Dictionary<string, object>(parsed.Count, StringComparer.OrdinalIgnoreCase);
                    foreach (var kvp in parsed)
                    {
                        object value = kvp.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => kvp.Value.GetString()!,
                            System.Text.Json.JsonValueKind.Number => kvp.Value.TryGetInt64(out var l) ? l : kvp.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => kvp.Value.ToString()
                        };
                        dict[kvp.Key] = value;
                    }
                    upstreamSignals = dict;
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to parse upstream signals header");
            }
        }

        // Hoist gateway-forwarded signatures into the signals dict so downstream consumers
        // see them via ev.Signals (no parallel HttpContext.Items store). Mutate the signals
        // dict before constructing the evidence record so it is part of the canonical surface.
        var upstreamPrimarySig = context.Request.Headers["X-Bot-Detection-PrimarySignature"].FirstOrDefault();
        if (!string.IsNullOrEmpty(upstreamPrimarySig))
        {
            var mutableSignals = upstreamSignals as Dictionary<string, object>
                ?? new Dictionary<string, object>(upstreamSignals, StringComparer.OrdinalIgnoreCase);
            mutableSignals[SignalKeys.PrimarySignature] = upstreamPrimarySig;
            mutableSignals[SignalKeys.SignatureMultifactor] = new Dashboard.MultiFactorSignatures
            {
                PrimarySignature = upstreamPrimarySig,
                IpSignature = context.Request.Headers["X-Bot-Detection-IpSignature"].FirstOrDefault(),
                UaSignature = context.Request.Headers["X-Bot-Detection-UaSignature"].FirstOrDefault(),
                ClientSideSignature = context.Request.Headers["X-Bot-Detection-ClientSideSignature"].FirstOrDefault(),
            };
            upstreamSignals = mutableSignals;
        }

        // Build AggregatedEvidence with ledger so Contributions property works
        var evidence = new AggregatedEvidence
        {
            Ledger = ledger,
            BotProbability = botProbability,
            Confidence = confidence,
            RiskBand = riskBand,
            PrimaryBotType = botType,
            PrimaryBotName = botName,
            Signals = upstreamSignals,
            TotalProcessingTimeMs = processingMs,
            PolicyName = "upstream",
            TriggeredActionPolicyName = actionName,
            ContributingDetectors = contributingDetectorNames
        };

        // Populate HttpContext.Items (same keys as PopulateContextFromAggregated)
        context.Items[AggregatedEvidenceKey] = evidence;
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = botProbability; // Legacy: holds probability for backward compat
        context.Items[BotProbabilityKey] = botProbability;
        context.Items[DetectionConfidenceKey] = confidence;
        context.Items[BotTypeKey] = botType;
        context.Items[BotNameKey] = botName;
        context.Items[PolicyNameKey] = "upstream";
        context.Items[DetectionReasonsKey] = detectionReasons;
        if (!string.IsNullOrEmpty(category))
            context.Items[BotCategoryKey] = category;

        // Create legacy result for compatibility with views/TagHelpers/extension methods.
        // BotName carries the canonical identity name regardless of bot vs human --
        // DetectionBroadcastMiddleware.BuildDetectionFromUpstream reads result.BotName to
        // produce the dashboard's persisted BotName, and gating it on isBot here was the
        // single point that nulled every human signature row in upstream-trusted mode.
        // BotType stays gated (a human has no bot type) but the display name is universal.
        var legacyResult = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = botProbability,
            BotType = isBot ? botType : null,
            BotName = botName,
            Reasons = detectionReasons
        };
        context.Items[BotDetectionResultKey] = legacyResult;

        _logger.LogDebug(
            "Trusted upstream detection for {Path}: isBot={IsBot}, probability={Probability:F2}, risk={Risk}, detectors={DetectorCount}",
            context.Request.Path, isBot, botProbability, riskBand, contributingDetectorNames.Count);

        return true;
    }

    /// <summary>
    ///     JSON model for parsing upstream contribution headers.
    /// </summary>
    private sealed class UpstreamContribution
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public double ConfidenceDelta { get; set; }
        public double Weight { get; set; }
        public double Contribution { get; set; }
        public string? Reason { get; set; }
        public double ExecutionTimeMs { get; set; }
        public int Priority { get; set; }
    }

    #endregion

    #region Context Population

    private void PopulateContextFromAggregated(
        HttpContext context,
        AggregatedEvidence result,
        string policyName)
    {
        // Store full aggregated result
        context.Items[AggregatedEvidenceKey] = result;
        context.Items[PolicyNameKey] = policyName;
        context.Items[PolicyActionKey] = result.PolicyAction;

        // Map to legacy keys for compatibility
        // Use configurable BotThreshold for consistency with blocking logic
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        var isBot = result.BotProbability >= _options.BotThreshold;
#pragma warning restore CS0618
        context.Items[IsBotKey] = isBot;
        context.Items[BotConfidenceKey] = result.BotProbability; // Legacy: holds probability for backward compat
        context.Items[BotProbabilityKey] = result.BotProbability;
        context.Items[DetectionConfidenceKey] = result.Confidence;
        context.Items[BotTypeKey] = result.PrimaryBotType;
        context.Items[BotNameKey] = result.PrimaryBotName;

        // Primary category from highest-contributing category (linear scan, no sort allocation)
        if (result.CategoryBreakdown.Count > 0)
        {
            string? bestKey = null;
            var bestScore = double.MinValue;
            foreach (var kv in result.CategoryBreakdown)
            {
                var abs = Math.Abs(kv.Value.Score);
                if (abs > bestScore) { bestScore = abs; bestKey = kv.Key; }
            }
            context.Items[BotCategoryKey] = bestKey;
        }

        // Also create a legacy BotDetectionResult for compatibility
        var contributions = result.Contributions;
        var reasons = new List<DetectionReason>(contributions.Count);
        foreach (var c in contributions)
            reasons.Add(new DetectionReason { Category = c.Category, Detail = c.Reason, ConfidenceImpact = c.ConfidenceDelta });
        var legacyResult = new BotDetectionResult
        {
            IsBot = isBot,
            ConfidenceScore = result.BotProbability,
            BotType = isBot ? result.PrimaryBotType : null,
            BotName = isBot ? result.PrimaryBotName : null,
            Reasons = reasons,
            ProcessingTimeMs = result.TotalProcessingTimeMs
        };
        context.Items[BotDetectionResultKey] = legacyResult;

        // Record into IBotDetectionService.GetStatistics() so the diagnostic
        // endpoints (/bot-detection/stats, /health) reflect orchestrator-
        // driven traffic. Otherwise the counter sits at zero forever because
        // the legacy IBotDetectionService.DetectAsync entry point is unused
        // when the orchestrator is in charge.
        context.RequestServices
            .GetService<IBotDetectionService>()
            ?.RecordDetection(legacyResult);
    }

    private static void PopulateContextItems(HttpContext context, BotDetectionResult result)
    {
        // Full result object (for complete access)
        context.Items[BotDetectionResultKey] = result;

        // Individual properties (for quick access without casting)
        context.Items[IsBotKey] = result.IsBot;
        context.Items[BotConfidenceKey] = result.ConfidenceScore;
        context.Items[BotTypeKey] = result.BotType;
        context.Items[BotNameKey] = result.BotName;
        context.Items[DetectionReasonsKey] = result.Reasons;

        // Primary category (from highest-confidence reason)
        if (result.Reasons.Count > 0)
        {
            var primaryReason = result.Reasons.OrderByDescending(r => r.ConfidenceImpact).First();
            context.Items[BotCategoryKey] = primaryReason.Category;
        }
    }

    #endregion

    #region Response Mutation

    private async Task InvokeNextWithResponseMutationAsync(HttpContext context)
    {
        if (!IsResponsePiiMaskActionRequested(context))
        {
            await _next(context);
            return;
        }

        if (!_options.ResponsePiiMasking.Enabled)
        {
            EmitResponsePiiMaskingSkippedSignal(context, "feature-disabled");
            _logger.LogInformation(
                "[ACTION] mask-pii requested for {Path} but BotDetection:ResponsePiiMasking:Enabled is false. Continuing unmodified.",
                context.Request.Path);
            await _next(context);
            return;
        }

        var masker = context.RequestServices.GetService<IResponsePiiMasker>();
        if (masker == null)
        {
            EmitResponsePiiMaskingSkippedSignal(context, "service-missing");
            _logger.LogWarning(
                "[ACTION] mask-pii requested for {Path} but no IResponsePiiMasker is registered. Continuing unmodified.",
                context.Request.Path);
            await _next(context);
            return;
        }

        // Content length may change after masking. Force chunked/unknown length response.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove("Content-Length");
            context.Response.Headers.TryAdd("X-Bot-Response-Action", "mask-pii");
            return Task.CompletedTask;
        });

        var originalBody = context.Response.Body;
        var maskingStream = new SlidingWindowPiiMaskingStream(
            originalBody,
            masker,
            () => masker.ShouldMaskContent(context.Response.ContentType, context.Response.Headers));

        context.Response.Body = maskingStream;
        try
        {
            await _next(context);
            await maskingStream.CompleteAsync(context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        EmitResponsePiiMaskSignals(
            context,
            maskingStream.RedactionCount,
            mode: "sliding-window",
            failOpen: maskingStream.FellBackToPassthrough);
    }

    private static bool IsResponsePiiMaskActionRequested(HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetection.Action", out var action) &&
            action is string marker &&
            (marker.Equals("mask-pii", StringComparison.OrdinalIgnoreCase) ||
             marker.Equals("strip-pii", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private void EnsureMaliciousFallbackMaskPii(HttpContext context)
    {
        if (!_options.ResponsePiiMasking.Enabled || !_options.ResponsePiiMasking.AutoApplyForHighConfidenceMalicious)
            return;

        if (IsResponsePiiMaskActionRequested(context))
            return;

        if (!context.Items.TryGetValue(AggregatedEvidenceKey, out var evidenceObj) ||
            evidenceObj is not AggregatedEvidence evidence)
            return;

        // Safety net: high-confidence malicious traffic that is still allowed through
        // should have response PII stripped to reduce exfiltration surface.
        if (evidence.PrimaryBotType != BotType.MaliciousBot)
            return;

        if (evidence.BotProbability < _options.ResponsePiiMasking.AutoApplyBotProbabilityThreshold ||
            evidence.Confidence < _options.ResponsePiiMasking.AutoApplyConfidenceThreshold)
            return;

        context.Items["BotDetection.Action"] = "mask-pii";
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
            context.Items[AggregatedEvidenceKey] = evidence with
            {
                TriggeredActionPolicyName = "mask-pii"
            };

        _logger.LogWarning(
            "[ACTION] Auto-applied mask-pii for high-confidence malicious request on {Path} (risk={Risk:F2}, confidence={Confidence:F2})",
            context.Request.Path, evidence.BotProbability, evidence.Confidence);
    }

    private static void EmitResponsePiiMaskingSkippedSignal(HttpContext context, string reason)
    {
        context.Items["BotDetection.ResponsePiiMasking.Attempted"] = false;
        context.Items["BotDetection.ResponsePiiMasking.Skipped"] = true;
        context.Items["BotDetection.ResponsePiiMasking.SkipReason"] = reason;

        if (context.Items.TryGetValue(AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            var updatedSignals = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase)
            {
                ["response.pii_masking.attempted"] = false,
                ["response.pii_masking.skipped"] = true,
                ["response.pii_masking.skip_reason"] = reason
            };

            context.Items[AggregatedEvidenceKey] = evidence with
            {
                Signals = updatedSignals
            };
        }
    }

    private void EmitResponsePiiMaskSignals(
        HttpContext context,
        int redactionCount,
        string mode,
        bool failOpen)
    {
        context.Items["BotDetection.ResponsePiiMasking.Attempted"] = true;
        context.Items["BotDetection.ResponsePiiMasking.RedactionCount"] = redactionCount;
        context.Items["BotDetection.ResponsePiiMasking.Mode"] = mode;
        context.Items["BotDetection.ResponsePiiMasking.FailOpen"] = failOpen;
        context.Items["BotDetection.ResponsePiiMasking.Masked"] = redactionCount > 0;

        if (context.Items.TryGetValue(AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            var updatedSignals = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase)
            {
                ["response.pii_masking.attempted"] = true,
                ["response.pii_masking.masked"] = redactionCount > 0,
                ["response.pii_masking.redaction_count"] = redactionCount,
                ["response.pii_masking.mode"] = mode,
                ["response.pii_masking.fail_open"] = failOpen
            };

            var updatedDetectors = new HashSet<string>(evidence.ContributingDetectors, StringComparer.OrdinalIgnoreCase)
            {
                "ResponsePiiMasker"
            };

            var updatedEvidence = evidence with
            {
                Signals = updatedSignals,
                ContributingDetectors = updatedDetectors
            };

            updatedEvidence.Ledger?.AddContribution(new DetectionContribution
            {
                DetectorName = "ResponsePiiMasker",
                Category = "ResponseAction",
                ConfidenceDelta = 0,
                Weight = 1.0,
                Reason = redactionCount > 0
                    ? $"Response PII masked ({redactionCount} entities, mode={mode}, failOpen={failOpen})"
                    : $"Response PII masking ran with no entities found (mode={mode}, failOpen={failOpen})",
                ProcessingTimeMs = 0,
                Priority = 998
            });

            context.Items[AggregatedEvidenceKey] = updatedEvidence;
        }

        if (redactionCount > 0)
            _logger.LogWarning(
                "[ACTION] mask-pii applied on {Path}: redactions={Redactions}, mode={Mode}, failOpen={FailOpen}",
                context.Request.Path, redactionCount, mode, failOpen);
        else
            _logger.LogInformation(
                "[ACTION] mask-pii executed on {Path}: no PII entities matched (mode={Mode}, failOpen={FailOpen})",
                context.Request.Path, mode, failOpen);
    }

    #endregion

    #region Post-Response Status Analysis

    /// <summary>
    ///     Fail2ban-style post-response analysis.
    ///     After next() returns, the response status code is available.
    ///     Suspicious status codes (404, 401, 500, etc.) boost the detection score.
    ///     Authenticated successful responses reduce suspicion for marginal cases only.
    ///     Updates AggregatedEvidence in HttpContext.Items so downstream middleware
    ///     (DetectionBroadcastMiddleware) sees the response-aware score.
    ///     All thresholds configurable via BotDetection:ResponseStatusBoost in appsettings.json.
    /// </summary>
    private void ApplyResponseStatusBoost(HttpContext context)
    {
        var boostOpts = _options.ResponseStatusBoost;
        if (!boostOpts.Enabled)
            return;

        // Only applies when detection actually ran
        if (!context.Items.TryGetValue(AggregatedEvidenceKey, out var evidenceObj) ||
            evidenceObj is not AggregatedEvidence evidence)
            return;

        var statusCode = context.Response.StatusCode;
        var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;

        // Compute delta based on response status code (all values from config)
        var (delta, reason) = statusCode switch
        {
            404 => (boostOpts.NotFoundDelta,
                $"Response 404 Not Found on {context.Request.Path}"),
            401 when !isAuthenticated => (boostOpts.UnauthorizedDelta,
                "Response 401 Unauthorized - unauthenticated probe"),
            403 when !isAuthenticated => (boostOpts.ForbiddenDelta,
                "Response 403 Forbidden - access denied"),
            >= 500 and < 600 => (boostOpts.ServerErrorDelta,
                $"Response {statusCode} Server Error triggered"),
            410 => (boostOpts.GoneDelta,
                "Response 410 Gone - probing removed resource"),
            405 => (boostOpts.MethodNotAllowedDelta,
                $"Response 405 Method Not Allowed on {context.Request.Path}"),
            // Authenticated successful response: clear suspicion for MARGINAL cases only.
            // High-confidence bots (> MaxProbability) are not cleared even if authenticated,
            // preventing abuse by authenticated bot accounts.
            >= 200 and < 300 when isAuthenticated
                && evidence.BotProbability > boostOpts.AuthenticatedClearThreshold
                && evidence.BotProbability <= boostOpts.AuthenticatedClearMaxProbability
                => (boostOpts.AuthenticatedClearDelta,
                    "Authenticated user - successful response clears suspicion"),
            _ => (0.0, (string?)null)
        };

        if (delta == 0.0 || reason == null)
            return;

        var newProbability = Math.Clamp(evidence.BotProbability + delta, 0.0, 1.0);

        // Add contribution to the ledger so it shows in detector breakdown
        var contribution = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "ResponseStatusBoost",
            Category = "ResponseStatus",
            ConfidenceDelta = delta,
            Weight = 1.0,
            Reason = reason,
            ProcessingTimeMs = 0,
            Priority = 999 // Runs after all other detectors
        };

        // Recalculate risk band
        var newRiskBand = newProbability switch
        {
            >= 0.85 => RiskBand.VeryHigh,
            >= 0.7 => RiskBand.High,
            >= 0.5 => RiskBand.Medium,
            >= 0.3 => RiskBand.Elevated,
            >= 0.15 => RiskBand.Low,
            _ => RiskBand.VeryLow
        };

        // Build updated signals dictionary (handles immutable Signals safely)
        var signalDict = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase);
        signalDict["response.status_code"] = statusCode;
        signalDict["response.status_boost"] = delta;
        if (isAuthenticated)
            signalDict["response.authenticated"] = true;

        // Update the evidence with boosted probability and enriched signals
        var detectors = new HashSet<string>(evidence.ContributingDetectors, StringComparer.OrdinalIgnoreCase);
        detectors.Add("ResponseStatusBoost");

        var updatedEvidence = evidence with
        {
            BotProbability = newProbability,
            RiskBand = newRiskBand,
            ContributingDetectors = detectors,
            Signals = signalDict
        };

        // Add contribution to ledger AFTER creating updatedEvidence
        // (both share same Ledger reference since it's a ref type)
        updatedEvidence.Ledger?.AddContribution(contribution);

        // Write back to context for DetectionBroadcastMiddleware
        context.Items[AggregatedEvidenceKey] = updatedEvidence;
        context.Items[BotProbabilityKey] = newProbability;
        context.Items[BotConfidenceKey] = newProbability;
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        context.Items[IsBotKey] = newProbability >= _options.BotThreshold;
#pragma warning restore CS0618

        _logger.LogInformation(
            "[RESPONSE-BOOST] {Path} status={Status} delta={Delta:+0.00;-0.00} " +
            "prob={OldProb:F2}->{NewProb:F2} auth={Auth}: {Reason}",
            context.Request.Path, statusCode, delta,
            evidence.BotProbability, newProbability,
            isAuthenticated, reason);
    }

    #endregion

    #region Response Recording

    private sealed record RequestSnapshot(
        string ClientId,
        string RequestId,
        string Path,
        string Method);

    private RequestSnapshot CaptureRequestSnapshot(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        return new RequestSnapshot(
            ClientId: $"{ip}:{GetHash(ua)}",
            RequestId: context.TraceIdentifier,
            Path: context.Request.Path.Value ?? "/",
            Method: context.Request.Method);
    }

    /// <summary>
    ///     Builds a ResponseSignal from pre-captured values.
    ///     Returns null when the action is Block, Challenge, or Throttle to prevent
    ///     positive feedback loops where our own synthetic responses raise bot scores.
    /// </summary>
    private static ResponseSignal? BuildResponseSignal(
        string clientId,
        string requestId,
        string path,
        string method,
        int statusCode,
        long contentLength,
        string? contentType,
        double processingTimeMs,
        double requestBotProbability,
        Policies.DetectionPolicyAction? action)
    {
        if (action is Policies.DetectionPolicyAction.Block
            or Policies.DetectionPolicyAction.Challenge
            or Policies.DetectionPolicyAction.Throttle)
            return null;

        return new ResponseSignal
        {
            RequestId = requestId,
            ClientId = clientId,
            Timestamp = DateTimeOffset.UtcNow,
            StatusCode = statusCode,
            ResponseBytes = contentLength,
            Path = path,
            Method = method,
            ProcessingTimeMs = processingTimeMs,
            RequestBotProbability = requestBotProbability,
            InlineAnalysis = false,
            BodySummary = new ResponseBodySummary
            {
                IsPresent = contentLength > 0,
                Length = (int)contentLength,
                ContentType = contentType
            }
        };
    }

    /// <summary>
    ///     Test shim: exposes BuildResponseSignal for unit testing.
    /// </summary>
    internal static ResponseSignal? BuildResponseSignalForTest(
        string clientId, string requestId, string path, string method,
        int statusCode, long contentLength, string? contentType,
        double processingTimeMs, double requestBotProbability, Policies.DetectionPolicyAction? action)
        => BuildResponseSignal(clientId, requestId, path, method,
            statusCode, contentLength, contentType,
            processingTimeMs, requestBotProbability, action);

    /// <summary>
    /// Computes a stable hash for the input string.
    /// Uses XxHash32 for performance - stable across app restarts.
    /// </summary>
    private static string GetHash(string input)
    {
        if (input.Length == 0) return "empty";
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = System.IO.Hashing.XxHash32.HashToUInt32(bytes);
        return hash.ToString("X8");
    }

    #endregion

    #region Detection failure handling

    /// <summary>
    ///     Result of the detection-failure applier: whether to continue the pipeline
    ///     (FailOpen / LogOnly) or short-circuit (FailClosed).
    /// </summary>
    public readonly record struct DetectionFailureResult(bool ContinuePipeline);

    /// <summary>
    ///     Small applier used by the middleware's try-catch around the orchestrator call.
    ///     Split out as a public type so the failure semantics are unit-testable without
    ///     the full middleware fixture.
    /// </summary>
    public sealed class DetectionFailureHandler
    {
        private readonly FailureMode _mode;
        public DetectionFailureHandler(FailureMode mode) => _mode = mode;

        /// <summary>
        ///     Apply the configured FailureMode to the current response. Writes an
        ///     X-StyloBot-Failed header (carrying the exception's type name) on all modes
        ///     for observability. FailClosed sets status 503 and returns ContinuePipeline=false.
        /// </summary>
        public DetectionFailureResult Apply(HttpContext ctx, Exception ex)
        {
            ctx.Response.Headers["X-StyloBot-Failed"] = ex.GetType().Name;
            switch (_mode)
            {
                case FailureMode.FailClosed:
                    ctx.Response.StatusCode = 503;
                    return new DetectionFailureResult(ContinuePipeline: false);
                case FailureMode.LogOnly:
                case FailureMode.FailOpen:
                default:
                    return new DetectionFailureResult(ContinuePipeline: true);
            }
        }
    }

    /// <summary>Factory used by tests and the middleware's catch block.</summary>
    public static DetectionFailureHandler HandleDetectionFailureFor(DetectionPolicy policy)
        => new(policy.OnFailure);

    #endregion
}

/// <summary>
///     Response object for blocked requests (AOT-compatible)
/// </summary>
internal record BlockedResponse(string Error, string Reason, double RiskScore, string Policy);

/// <summary>
///     Response object for challenge requests (AOT-compatible)
/// </summary>
internal record ChallengeResponse(string Error, string ChallengeType, double RiskScore);

/// <summary>
///     Extension methods for adding bot detection middleware.
/// </summary>
public static class BotDetectionMiddlewareExtensions
{
    /// <summary>
    ///     Add bot detection middleware to the pipeline.
    ///     Should be called after UseRouting() but before UseAuthorization().
    /// </summary>
    /// <example>
    ///     app.UseRouting();
    ///     app.UseBotDetection();
    ///     app.UseAuthorization();
    /// </example>
    public static IApplicationBuilder UseBotDetection(this IApplicationBuilder builder)
    {
        // Tunnel-environment inspector observes the first N requests and snapshots
        // what TLS / JA3 / proxy signals reach the gateway, so the dashboard can
        // show an actionable banner when a tunnel is hiding the fingerprint surface.
        // Runs first so its observation covers every request the gateway sees,
        // including ones that later short-circuit on EndpointPolicy / lifecycle.
        // After MinSamples the lambda is a single Volatile.Read + return inside
        // Observe -- not worth its own middleware class.
        builder.Use(async (ctx, next) =>
        {
            var inspector = ctx.RequestServices.GetService<Proxy.ITunnelEnvironmentInspector>();
            inspector?.Observe(ctx);
            await next();
        });

        builder.UseMiddleware<AssetHashMiddleware>();
        // Endpoint policy resolver runs FIRST: hard operator rules
        // (block POST /xmlrpc.php, throttle DELETE /api/*, etc.) short-circuit
        // before any detection cost. Throttle/LogOnly continue the pipeline.
        builder.UseMiddleware<EndpointPolicies.EndpointPolicyMiddleware>();
        // Path lifecycle recorder wraps the inner pipeline so it observes the
        // FINAL response status code (post-detection, post-action-policy).
        // Feeds the honeypot threat scorer's "this path used to serve 2xx,
        // now scanners are still probing it" branch.
        builder.UseMiddleware<Lifecycle.PathLifecycleMiddleware>();
        // Honeypot tagger runs BEFORE BotDetectionMiddleware so the
        // classification survives any early-exit short-circuit later in the
        // pipeline (FastPathReputation, SignatureVerdictGate Skip).
        builder.UseMiddleware<Honeypot.HoneypotPathTagger>();
        return builder.UseMiddleware<BotDetectionMiddleware>();
    }
}
