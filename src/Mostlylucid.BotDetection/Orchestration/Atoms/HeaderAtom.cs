using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that analyses HTTP header shape for
///     bot / browser separation. Reads Sec-Fetch-* attestations, checks for
///     essential browser headers, and calibrates missing-header penalties
///     against the deployment norm learned by
///     <see cref="DeploymentNormTracker"/>.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>HeaderContributor</c>. Priority 10 -- first-wave, no required
///         signals.
///     </para>
///     <para>
///         Reads UA + headers straight from <see cref="HttpContext"/> since
///         this atom runs early enough that upstream sensors' hints aren't
///         guaranteed to be on the sink yet. All emitted signals are
///         low-cardinality protocol values or booleans -- no PII, so
///         Model-2 hints are the right carrier.
///     </para>
///     <para>
///         Same programmatic-attestation carve-out shape as the contributor:
///         Sec-Fetch-*, X-Requested-With + fetch mode, X-Api-Key context,
///         WebSocket upgrade, and Service-Worker: script all attenuate the
///         "missing browser headers" penalty so legitimate API / fetch
///         traffic doesn't get flagged.
///     </para>
/// </remarks>
public sealed class HeaderAtom : DetectorAtomBase
{
    private readonly ILogger<HeaderAtom> _logger;
    private readonly DeploymentNormTracker _norms;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly int _populationMinSamples;
    private readonly double _populationRateThreshold;

    public HeaderAtom(
        ILogger<HeaderAtom> logger,
        IDetectorConfigProvider configProvider,
        DeploymentNormTracker norms,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Header", category: "Header")
    {
        _logger = logger;
        _configProvider = configProvider;
        _norms = norms;
        _httpContextAccessor = httpContextAccessor;
        _populationMinSamples = configProvider.GetParameter(Name, "population_min_samples", 20);
        _populationRateThreshold = configProvider.GetParameter(Name, "population_rate_threshold", 0.7);
    }

    public override int Priority => 10;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double MissingHeaderPenalty => _configProvider.GetParameter(Name, "missing_header_penalty", 0.1);
    private double OrderAnomalyPenalty => _configProvider.GetParameter(Name, "order_anomaly_penalty", 0.15);
    private int MinHeaderCount => _configProvider.GetParameter(Name, "min_header_count", 3);
    private double ConfidenceBotDetected => _configProvider.GetParameter(Name, "confidence_bot_detected", 0.6);
    private double ConfidenceStrongSignal => _configProvider.GetParameter(Name, "confidence_strong_signal", 0.75);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var headers = context.Request.Headers;
        var contributions = new List<DetectionContribution>();

        // WebSocket upgrades legitimately omit browser headers (RFC 6455).
        var isWebSocketUpgrade = sink.ReadBoolHint(SignalKeys.TransportIsUpgrade)
                                 || IsWebSocketUpgrade(context.Request);
        sink.Raise($"{SignalKeys.HeaderIsWebSocketUpgrade}:{(isWebSocketUpgrade ? "true" : "false")}", sessionId);

        // Sec-Fetch-* attestations (W3C Fetch Metadata Request Headers).
        var secFetchSite = headers["Sec-Fetch-Site"].FirstOrDefault();
        var secFetchMode = headers["Sec-Fetch-Mode"].FirstOrDefault();
        var secFetchDest = headers["Sec-Fetch-Dest"].FirstOrDefault();
        var isSameOriginFetch = string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);

        sink.Raise($"{SignalKeys.HeaderSecFetchSite}:{secFetchSite ?? string.Empty}", sessionId);
        sink.Raise($"{SignalKeys.HeaderSecFetchMode}:{secFetchMode ?? string.Empty}", sessionId);
        sink.Raise($"{SignalKeys.HeaderSecFetchDest}:{secFetchDest ?? string.Empty}", sessionId);
        sink.Raise($"{SignalKeys.HeaderSecFetchSameOrigin}:{(isSameOriginFetch ? "true" : "false")}", sessionId);

        var isServiceWorkerFetch = headers.TryGetValue("Service-Worker", out var swHeader)
                                   && string.Equals(swHeader.ToString(), "script", StringComparison.OrdinalIgnoreCase);
        sink.Raise($"{SignalKeys.HeaderIsServiceWorkerFetch}:{(isServiceWorkerFetch ? "true" : "false")}", sessionId);

        var hasFetchMetadata = !string.IsNullOrEmpty(secFetchSite);
        var hasApiKey = context.Items.ContainsKey("BotDetection.ApiKeyContext");
        var isProgrammatic = hasFetchMetadata || hasApiKey || isWebSocketUpgrade || isServiceWorkerFetch;

        sink.Raise($"{SignalKeys.ProgrammaticFetchAttestation}:{(hasFetchMetadata ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.ProgrammaticApiKey}:{(hasApiKey ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.ProgrammaticRequest}:{(isProgrammatic ? "true" : "false")}", sessionId);

        var hasAcceptLanguage = headers.ContainsKey("Accept-Language");
        var hasAccept = headers.ContainsKey("Accept");
        var hasAcceptEncoding = headers.ContainsKey("Accept-Encoding");

        sink.Raise($"{SignalKeys.HeaderHasAcceptLanguage}:{(hasAcceptLanguage ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.HeaderHasAccept}:{(hasAccept ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.HeaderHasAcceptEncoding}:{(hasAcceptEncoding ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.HeaderCount}:{headers.Count}", sessionId);

        var userAgent = context.Request.Headers.UserAgent.ToString();
        var looksLikeBrowser = userAgent.Contains("Mozilla/")
            && (userAgent.Contains("Chrome") || userAgent.Contains("Firefox")
                || userAgent.Contains("Safari") || userAgent.Contains("Edge"));

        var uaBucket = sink.ReadHint(SignalKeys.UserAgentFamily) ?? (looksLikeBrowser ? "browser" : "non-browser");

        // Missing Accept -- calibrated against deployment norm.
        if (!hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            var eval = _norms.Evaluate(
                DeploymentNormTracker.Features.AcceptHeader, uaBucket, present: false,
                _populationMinSamples, _populationRateThreshold,
                out var acceptRate, out var acceptSamples);

            sink.Raise($"{SignalKeys.HeaderPopulationAcceptRate}:{acceptRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            contributions.Add(eval switch
            {
                NormEvaluation.WarmingUp => DetectionContribution.Info(Name, Category,
                    $"Missing Accept header; deployment still calibrating ({_norms.TotalRequests} requests seen)"),
                NormEvaluation.BelowNorm => DetectionContribution.Info(Name, Category,
                    $"Missing Accept header; deployment norm is low Accept rate ({acceptRate:P0} over {acceptSamples} samples)"),
                _ => new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = acceptSamples < _populationMinSamples
                        ? ConfidenceBotDetected * 0.5
                        : ConfidenceBotDetected * acceptRate,
                    Weight = 1.0,
                    Reason = "Missing Accept header",
                    BotType = BotType.Unknown.ToString()
                }
            });
        }
        else if (hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            _norms.Record(DeploymentNormTracker.Features.AcceptHeader, uaBucket, present: true);
        }

        // Missing Accept-Language with browser UA -- calibrated against norm.
        if (looksLikeBrowser && !hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            var eval = _norms.Evaluate(
                DeploymentNormTracker.Features.AcceptLanguage, uaBucket, present: false,
                _populationMinSamples, _populationRateThreshold,
                out var langRate, out var langSamples);

            sink.Raise($"{SignalKeys.HeaderPopulationAcceptLanguageRate}:{langRate.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
            contributions.Add(eval switch
            {
                NormEvaluation.WarmingUp => DetectionContribution.Info(Name, Category,
                    $"Browser UA without Accept-Language; deployment still calibrating ({_norms.TotalRequests} requests seen)"),
                NormEvaluation.BelowNorm => DetectionContribution.Info(Name, Category,
                    $"Browser UA without Accept-Language; deployment norm is low language rate ({langRate:P0} over {langSamples} samples)"),
                _ => new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = langSamples < _populationMinSamples
                        ? ConfidenceStrongSignal * 0.5
                        : ConfidenceStrongSignal * langRate,
                    Weight = 1.0,
                    Reason = "Browser User-Agent without Accept-Language",
                    BotType = BotType.Scraper.ToString()
                }
            });
        }
        else if (looksLikeBrowser && hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
        {
            _norms.Record(DeploymentNormTracker.Features.AcceptLanguage, uaBucket, present: true);
        }

        // Proxy header presence
        var hasXForwardedFor = headers.ContainsKey("X-Forwarded-For");
        var hasVia = headers.ContainsKey("Via");
        sink.Raise($"{SignalKeys.HeaderHasProxyHeaders}:{((hasXForwardedFor || hasVia) ? "true" : "false")}", sessionId);

        var headerCount = headers.Count;
        if (headerCount < MinHeaderCount && !isWebSocketUpgrade && !hasApiKey)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ConfidenceStrongSignal,
                Weight = 1.0,
                Reason = $"Very few headers ({headerCount})",
                BotType = BotType.Scraper.ToString()
            });
        }

        // AJAX request without Accept-Language, no fetch-metadata / API-key carve-out
        if (headers.ContainsKey("X-Requested-With")
            && headers["X-Requested-With"].ToString() == "XMLHttpRequest"
            && !hasAcceptLanguage && !hasFetchMetadata && !hasApiKey)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = ConfidenceBotDetected,
                Weight = 1.0,
                Reason = "AJAX request without Accept-Language",
                BotType = BotType.Scraper.ToString()
            });
        }

        // Same-origin browser fetch is a strong human attestation.
        if (isSameOriginFetch && looksLikeBrowser)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.4,
                Weight = 1.0,
                Reason = "Same-origin browser fetch - Sec-Fetch-Site attestation present"
            });
        }
        else if (hasFetchMetadata && !isSameOriginFetch)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.3,
                Weight = 1.0,
                Reason = $"Browser fetch metadata present (Sec-Fetch-Site: {secFetchSite})"
            });
        }

        if (isServiceWorkerFetch)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.35,
                Weight = 1.0,
                Reason = "Service-Worker: script header - browser service worker registration fetch"
            });
        }

        if (hasApiKey)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.25,
                Weight = 1.0,
                Reason = "Valid API key - trusted programmatic client"
            });
        }

        var missingHeadersDetected =
            (!hasAccept && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
            || (looksLikeBrowser && !hasAcceptLanguage && !isWebSocketUpgrade && !hasFetchMetadata && !hasApiKey)
            || (headerCount < MinHeaderCount && !isWebSocketUpgrade && !hasApiKey);
        if (missingHeadersDetected)
            sink.Raise($"{SignalKeys.HeadersMissing}:true", sessionId);

        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.15,
                Weight = 1.0,
                Reason = isWebSocketUpgrade ? "WebSocket upgrade - header profile expected" : "Headers appear normal"
            });
        }

        if (contributions.Any(c => c.ConfidenceDelta > 0.05))
            sink.Raise($"{SignalKeys.HeadersSuspicious}:true", sessionId);

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    /// <summary>
    ///     Detects WebSocket upgrade requests (RFC 6455). Browsers legitimately
    ///     omit Accept, Accept-Language, Accept-Encoding, and Client Hints on
    ///     upgrades.
    /// </summary>
    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
               && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
    }
}
