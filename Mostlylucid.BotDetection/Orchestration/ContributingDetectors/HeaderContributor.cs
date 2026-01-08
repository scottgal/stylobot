using System.Collections.Immutable;
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
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        // Check for missing essential headers (from YAML: expected_browser_headers)
        var expectedHeaders = GetStringListParam("expected_browser_headers");
        var hasAcceptLanguage = headers.ContainsKey("Accept-Language");
        var hasAccept = headers.ContainsKey("Accept");
        var hasAcceptEncoding = headers.ContainsKey("Accept-Encoding");

        signals.Add("header.has_accept_language", hasAcceptLanguage);
        signals.Add("header.has_accept", hasAccept);
        signals.Add("header.has_accept_encoding", hasAcceptEncoding);
        signals.Add("header.count", headers.Count);

        // Missing Accept header - confidence from YAML
        if (!hasAccept)
            contributions.Add(BotContribution(
                    "Header",
                    "Missing Accept header",
                    confidenceOverride: ConfidenceBotDetected, // from YAML: defaults.confidence.bot_detected
                    botType: BotType.Unknown.ToString())
                with
                {
                    Signals = signals.ToImmutable()
                });

        // Missing Accept-Language with browser UA
        var userAgent = state.UserAgent ?? "";
        var looksLikeBrowser = userAgent.Contains("Mozilla/") &&
                               (userAgent.Contains("Chrome") || userAgent.Contains("Firefox") ||
                                userAgent.Contains("Safari") || userAgent.Contains("Edge"));

        if (looksLikeBrowser && !hasAcceptLanguage)
            contributions.Add(BotContribution(
                "Header",
                "Browser User-Agent without Accept-Language",
                confidenceOverride: ConfidenceStrongSignal, // from YAML: defaults.confidence.strong_signal
                botType: BotType.Scraper.ToString()));

        // Check for proxy headers (X-Forwarded-For, Via)
        var hasXForwardedFor = headers.ContainsKey("X-Forwarded-For");
        var hasVia = headers.ContainsKey("Via");
        signals.Add("header.has_proxy_headers", hasXForwardedFor || hasVia);

        // Check for unusual header count - threshold from YAML parameters
        var headerCount = headers.Count;
        if (headerCount < MinHeaderCount)
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
                "Headers appear normal") with { Signals = signals.ToImmutable() });

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }
}