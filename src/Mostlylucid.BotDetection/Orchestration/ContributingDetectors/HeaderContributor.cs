using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     HTTP Header analysis for bot detection.
///     Runs in the first wave (no dependencies).
///     Analyzes request headers for bot indicators.
///
///     Configuration loaded from: header.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:HeaderContributor:*
/// </summary>
public class HeaderContributor : ConfiguredContributorBase
{
    private readonly ILogger<HeaderContributor> _logger;
    private readonly DeploymentNormTracker _norms;
    private readonly int _populationMinSamples;
    private readonly double _populationRateThreshold;

    public HeaderContributor(
        ILogger<HeaderContributor> logger,
        IDetectorConfigProvider configProvider,
        DeploymentNormTracker norms)
        : base(configProvider)
    {
        _logger = logger;
        _norms = norms;
        _populationMinSamples = GetParam("population_min_samples", 20);
        _populationRateThreshold = GetParam("population_rate_threshold", 0.7);
    }

    public override string Name => "Header";
    public override int Priority => Manifest?.Priority ?? 10;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters (from YAML defaults.parameters)
    private double MissingHeaderPenalty => GetParam("missing_header_penalty", 0.1);
    private double OrderAnomalyPenalty => GetParam("order_anomaly_penalty", 0.15);
    private int MinHeaderCount => GetParam("min_header_count", 3);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var headers = state.HttpContext.Request.Headers;

        // WebSocket upgrade requests (RFC 6455) legitimately omit Accept, Accept-Language,
        // and Accept-Encoding headers. Don't penalize these missing headers on upgrades.
        // Prefer the TransportIsUpgrade signal from TransportProtocolContributor (Priority 5),
        // fall back to local detection if not yet available.
        var isWebSocketUpgrade = state.GetSignal<bool?>(SignalKeys.TransportIsUpgrade) ?? false;
        if (!isWebSocketUpgrade)
            isWebSocketUpgrade = IsWebSocketUpgrade(state.HttpContext.Request);
        state.WriteSignal(SignalKeys.HeaderIsWebSocketUpgrade, isWebSocketUpgrade);

        // Sec-Fetch-* headers (Fetch Metadata Request Headers, W3C spec).
        // Modern browsers send these on ALL requests - they attest the request's origin,
        // mode, and destination. Bots/scrapers rarely send them correctly.
        // A same-origin fetch() sends Sec-Fetch-Site: same-origin + Sec-Fetch-Mode: cors.
        var secFetchSite = headers["Sec-Fetch-Site"].FirstOrDefault();
        var secFetchMode = headers["Sec-Fetch-Mode"].FirstOrDefault();
        var secFetchDest = headers["Sec-Fetch-Dest"].FirstOrDefault();
        var isSameOriginFetch = string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);

        state.WriteSignal(SignalKeys.HeaderSecFetchSite, secFetchSite ?? "");
        state.WriteSignal(SignalKeys.HeaderSecFetchMode, secFetchMode ?? "");
        state.WriteSignal(SignalKeys.HeaderSecFetchDest, secFetchDest ?? "");
        state.WriteSignal(SignalKeys.HeaderSecFetchSameOrigin, isSameOriginFetch);

        // Service-Worker: script - browser explicitly fetching a service worker registration script.
        // Only real browsers send this; it is defined by the Service Workers spec and cannot
        // be mimicked without also sending the corresponding Sec-Fetch-* headers consistently.
        var isServiceWorkerFetch = headers.TryGetValue("Service-Worker", out var swHeader)
            && string.Equals(swHeader.ToString(), "script", StringComparison.OrdinalIgnoreCase);
        state.WriteSignal(SignalKeys.HeaderIsServiceWorkerFetch, isServiceWorkerFetch);

        // Write programmatic request attestation signals.
        // These are consumed by downstream detectors (Behavioral, ResponseBehavior,
        // Inconsistency) to avoid penalizing legitimate API/fetch traffic for
        // missing cookies, referer, regular timing, etc.
        var hasFetchMetadata = !string.IsNullOrEmpty(secFetchSite);
        var hasApiKey = state.HttpContext.Items.ContainsKey("BotDetection.ApiKeyContext");
        var isProgrammatic = hasFetchMetadata || hasApiKey || isWebSocketUpgrade || isServiceWorkerFetch;

        state.WriteSignal(SignalKeys.ProgrammaticFetchAttestation, hasFetchMetadata);
        state.WriteSignal(SignalKeys.ProgrammaticApiKey, hasApiKey);
        state.WriteSignal(SignalKeys.ProgrammaticRequest, isProgrammatic);

        // Check for missing essential headers (from YAML: expected_browser_headers)
        var expectedHeaders = GetStringListParam("expected_browser_headers");
        var hasAcceptLanguage = headers.ContainsKey("Accept-Language");
        var hasAccept = headers.ContainsKey("Accept");
        var hasAcceptEncoding = headers.ContainsKey("Accept-Encoding");

        state.WriteSignal(SignalKeys.HeaderHasAcceptLanguage, hasAcceptLanguage);
        state.WriteSignal(SignalKeys.HeaderHasAccept, hasAccept);
        state.WriteSignal(SignalKeys.HeaderHasAcceptEncoding, hasAcceptEncoding);
        state.WriteSignal(SignalKeys.HeaderCount, headers.Count);

        var userAgent = state.UserAgent ?? "";
        var looksLikeBrowser = userAgent.Contains("Mozilla/") &&
                               (userAgent.Contains("Chrome") || userAgent.Contains("Firefox") ||
                                userAgent.Contains("Safari") || userAgent.Contains("Edge"));

        var uaBucket = state.GetSignal<string>(SignalKeys.UserAgentFamily) ?? (looksLikeBrowser ? "browser" : "non-browser");

        // Missing Accept header - calibrated against deployment norm
        // Skip for WebSocket upgrades, Fetch Metadata requests, and API key holders.
        if (!hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            var eval = _norms.Evaluate(
                DeploymentNormTracker.Features.AcceptHeader, uaBucket, present: false,
                _populationMinSamples, _populationRateThreshold,
                out var acceptRate, out var acceptSamples);

            state.WriteSignal(SignalKeys.HeaderPopulationAcceptRate, acceptRate);
            contributions.Add(eval switch
            {
                NormEvaluation.WarmingUp =>
                    NeutralContribution("Header", $"Missing Accept header; deployment still calibrating ({_norms.TotalRequests} requests seen)"),
                NormEvaluation.BelowNorm =>
                    NeutralContribution("Header", $"Missing Accept header; deployment norm is low Accept rate ({acceptRate:P0} over {acceptSamples} samples)"),
                _ => BotContribution("Header", "Missing Accept header",
                    confidenceOverride: acceptSamples < _populationMinSamples ? ConfidenceBotDetected * 0.5 : ConfidenceBotDetected * acceptRate,
                    botType: BotType.Unknown.ToString())
            });
        }
        else if (hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            _norms.Record(DeploymentNormTracker.Features.AcceptHeader, uaBucket, present: true);
        }

        // Missing Accept-Language with browser UA - calibrated against deployment norm.
        // JS fetch() often omits Accept-Language even from real browsers.
        if (looksLikeBrowser && !hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            var eval = _norms.Evaluate(
                DeploymentNormTracker.Features.AcceptLanguage, uaBucket, present: false,
                _populationMinSamples, _populationRateThreshold,
                out var langRate, out var langSamples);

            state.WriteSignal(SignalKeys.HeaderPopulationAcceptLanguageRate, langRate);
            contributions.Add(eval switch
            {
                NormEvaluation.WarmingUp =>
                    NeutralContribution("Header", $"Browser UA without Accept-Language; deployment still calibrating ({_norms.TotalRequests} requests seen)"),
                NormEvaluation.BelowNorm =>
                    NeutralContribution("Header", $"Browser UA without Accept-Language; deployment norm is low language rate ({langRate:P0} over {langSamples} samples)"),
                _ => BotContribution("Header", "Browser User-Agent without Accept-Language",
                    confidenceOverride: langSamples < _populationMinSamples ? ConfidenceStrongSignal * 0.5 : ConfidenceStrongSignal * langRate,
                    botType: BotType.Scraper.ToString())
            });
        }
        else if (looksLikeBrowser && hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            _norms.Record(DeploymentNormTracker.Features.AcceptLanguage, uaBucket, present: true);
        }

        // Check for proxy headers (X-Forwarded-For, Via)
        var hasXForwardedFor = headers.ContainsKey("X-Forwarded-For");
        var hasVia = headers.ContainsKey("Via");
        state.WriteSignal(SignalKeys.HeaderHasProxyHeaders, hasXForwardedFor || hasVia);

        // Check for unusual header count - threshold from YAML parameters
        // WebSocket upgrades have fewer headers by design (Upgrade, Connection, Sec-WebSocket-*)
        var headerCount = headers.Count;
        if (headerCount < MinHeaderCount && !isWebSocketUpgrade && !hasApiKey)
            contributions.Add(BotContribution(
                "Header",
                $"Very few headers ({headerCount})",
                confidenceOverride: ConfidenceStrongSignal,
                botType: BotType.Scraper.ToString()));

        // Check for bot-specific headers
        // Skip for same-origin fetch - browser fetch() with X-Requested-With is legitimate
        if (headers.ContainsKey("X-Requested-With") &&
            headers["X-Requested-With"].ToString() == "XMLHttpRequest" &&
            !hasAcceptLanguage && !hasFetchMetadata && !hasApiKey)
            contributions.Add(BotContribution(
                "Header",
                "AJAX request without Accept-Language",
                botType: BotType.Scraper.ToString()));

        // Same-origin fetch with browser UA is a strong human signal - browsers attest
        // the request origin via Sec-Fetch-Site which bots can't easily forge while also
        // maintaining consistency across all other detection layers.
        if (isSameOriginFetch && looksLikeBrowser)
            contributions.Add(HumanContribution(
                "Header",
                "Same-origin browser fetch - Sec-Fetch-Site attestation present"));

        // Any Fetch Metadata (even cross-site) indicates a real browser - bots rarely
        // send Sec-Fetch-* headers, and when they do, cross-layer correlation catches them.
        else if (hasFetchMetadata && !isSameOriginFetch)
            contributions.Add(HumanContribution(
                "Header",
                $"Browser fetch metadata present (Sec-Fetch-Site: {secFetchSite})"));

        // Service-Worker: script - browser registering a service worker; only real browsers send this
        if (isServiceWorkerFetch)
            contributions.Add(HumanContribution(
                "Header",
                "Service-Worker: script header - browser service worker registration fetch"));

        // API key holder - trusted programmatic client, mild human signal
        if (hasApiKey)
            contributions.Add(HumanContribution(
                "Header",
                "Valid API key - trusted programmatic client"));

        var missingHeadersDetected =
            (!hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
            || (looksLikeBrowser && !hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
            || (headerCount < MinHeaderCount && !isWebSocketUpgrade && !hasApiKey);
        if (missingHeadersDetected)
            state.WriteSignal(SignalKeys.HeadersMissing, true);

        // No bot indicators found - emit human signal from YAML config
        if (contributions.Count == 0)
            contributions.Add(HumanContribution(
                "Header",
                isWebSocketUpgrade ? "WebSocket upgrade - header profile expected" : "Headers appear normal"));

        if (contributions.Any(c => c.ConfidenceDelta > 0.05))
            state.WriteSignal(SignalKeys.HeadersSuspicious, true);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Detects WebSocket upgrade requests (RFC 6455).
    ///     These legitimately omit Accept, Accept-Language, Accept-Encoding,
    ///     and Client Hints headers - browsers don't send them on WS upgrades.
    /// </summary>
    private static bool IsWebSocketUpgrade(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
               && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
    }
}
