using Microsoft.Extensions.Logging;
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

    public HeaderContributor(
        ILogger<HeaderContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
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
        var isWebSocketUpgrade = IsWebSocketUpgrade(state.HttpContext.Request);
        state.WriteSignal("header.is_websocket_upgrade", isWebSocketUpgrade);

        // Check for missing essential headers (from YAML: expected_browser_headers)
        var expectedHeaders = GetStringListParam("expected_browser_headers");
        var hasAcceptLanguage = headers.ContainsKey("Accept-Language");
        var hasAccept = headers.ContainsKey("Accept");
        var hasAcceptEncoding = headers.ContainsKey("Accept-Encoding");

        state.WriteSignal("header.has_accept_language", hasAcceptLanguage);
        state.WriteSignal("header.has_accept", hasAccept);
        state.WriteSignal("header.has_accept_encoding", hasAcceptEncoding);
        state.WriteSignal("header.count", headers.Count);

        // Missing Accept header - confidence from YAML
        // Skip for WebSocket upgrades which legitimately use Upgrade: websocket instead of Accept
        if (!hasAccept && !isWebSocketUpgrade)
            contributions.Add(BotContribution(
                    "Header",
                    "Missing Accept header",
                    confidenceOverride: ConfidenceBotDetected, // from YAML: defaults.confidence.bot_detected
                    botType: BotType.Unknown.ToString()));

        // Missing Accept-Language with browser UA
        // Skip for WebSocket upgrades which don't carry Accept-Language
        var userAgent = state.UserAgent ?? "";
        var looksLikeBrowser = userAgent.Contains("Mozilla/") &&
                               (userAgent.Contains("Chrome") || userAgent.Contains("Firefox") ||
                                userAgent.Contains("Safari") || userAgent.Contains("Edge"));

        if (looksLikeBrowser && !hasAcceptLanguage && !isWebSocketUpgrade)
            contributions.Add(BotContribution(
                "Header",
                "Browser User-Agent without Accept-Language",
                confidenceOverride: ConfidenceStrongSignal, // from YAML: defaults.confidence.strong_signal
                botType: BotType.Scraper.ToString()));

        // Check for proxy headers (X-Forwarded-For, Via)
        var hasXForwardedFor = headers.ContainsKey("X-Forwarded-For");
        var hasVia = headers.ContainsKey("Via");
        state.WriteSignal("header.has_proxy_headers", hasXForwardedFor || hasVia);

        // Check for unusual header count - threshold from YAML parameters
        // WebSocket upgrades have fewer headers by design (Upgrade, Connection, Sec-WebSocket-*)
        var headerCount = headers.Count;
        if (headerCount < MinHeaderCount && !isWebSocketUpgrade)
            contributions.Add(BotContribution(
                "Header",
                $"Very few headers ({headerCount})",
                confidenceOverride: ConfidenceStrongSignal,
                botType: BotType.Scraper.ToString()));

        // Check for bot-specific headers
        if (headers.ContainsKey("X-Requested-With") &&
            headers["X-Requested-With"].ToString() == "XMLHttpRequest" &&
            !hasAcceptLanguage)
            contributions.Add(BotContribution(
                "Header",
                "AJAX request without Accept-Language",
                botType: BotType.Scraper.ToString()));

        // No bot indicators found - emit human signal from YAML config
        if (contributions.Count == 0)
            contributions.Add(HumanContribution(
                "Header",
                isWebSocketUpgrade ? "WebSocket upgrade - header profile expected" : "Headers appear normal"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Detects WebSocket upgrade requests (RFC 6455).
    ///     These legitimately omit Accept, Accept-Language, Accept-Encoding,
    ///     and Client Hints headers — browsers don't send them on WS upgrades.
    /// </summary>
    private static bool IsWebSocketUpgrade(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
               && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase);
    }
}