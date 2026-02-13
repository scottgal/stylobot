using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     HTTP/2 fingerprinting contributor using AKAMAI-style fingerprinting.
///     Analyzes HTTP/2 frame sequences, settings, and priority to detect automation.
///     Best-in-breed approach:
///     - HTTP/2 SETTINGS frame analysis
///     - WINDOW_UPDATE patterns
///     - Stream priority patterns
///     - Header compression (HPACK) usage patterns
///     - Pseudoheader order fingerprinting
///     Raises signals for behavioral waveform:
///     - h2.settings_fingerprint
///     - h2.priority_pattern
///     - h2.window_update_behavior
///     - h2.pseudoheader_order
///     - h2.stream_behavior
///
///     Configuration loaded from: http2.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:Http2FingerprintContributor:*
/// </summary>
public class Http2FingerprintContributor : ConfiguredContributorBase
{
    // Known HTTP/2 fingerprints for different clients
    // Format: settings_frame values (key:value pairs)
    // Based on AKAMAI HTTP/2 fingerprinting research
    private static readonly Dictionary<string, string> KnownFingerprints = new()
    {
        // Chrome/Chromium fingerprints (versions 90+)
        { "1:65536,2:0,3:1000,4:6291456,6:262144", "Chrome_Desktop_90+" },
        { "1:65536,2:0,3:100,4:6291456,6:262144", "Chrome_Mobile" },
        { "1:262144,2:0,3:100,4:6291456,6:262144", "Chrome_Desktop_Latest" },

        // Firefox fingerprints
        { "1:65536,2:0,3:100,4:131072,5:16384", "Firefox_Desktop" },
        { "1:65536,2:0,3:250,4:131072,5:16384", "Firefox_Latest" },
        { "1:131072,3:100,4:131072,5:16384", "Firefox_Android" },

        // Safari/WebKit fingerprints
        { "1:32768,2:0,3:100,4:2097152", "Safari_Desktop" },
        { "2:0,3:100,4:2097152,8:1", "Safari_iOS" },
        { "1:65536,3:100,4:2097152", "Safari_Latest" },

        // Edge fingerprints (Chromium-based)
        { "1:65536,2:0,3:1000,4:6291456,6:262144", "Edge_Chromium" },

        // Opera fingerprints
        { "1:65536,2:0,3:1000,4:6291456,6:262144,8:1", "Opera_Chromium" },

        // ====================================
        // Bot/Automation Fingerprints
        // ====================================

        // Go net/http library (minimal settings)
        { "3:100,4:65536", "Go_HTTP2_Client" },
        { "3:1000,4:1048576", "Go_HTTP2_Custom" },
        { "4:4194304", "Go_Minimal" },

        // Python httpx/h2 library
        { "3:100,4:6291456", "Python_httpx" },
        { "1:65535,3:100,4:65535", "Python_h2_Library" },
        { "2:0,3:100,4:1048576", "Python_Custom_Stack" },

        // Node.js http2 module
        { "1:4096,3:100,4:65536", "Node_HTTP2_Bot" },
        { "3:100,4:16777215", "Node_HTTP2_Default" },
        { "1:65535,2:1,3:100,4:65535", "Node_HTTP2_Custom" },

        // cURL with HTTP/2 (libcurl)
        { "3:100,5:16384", "cURL_HTTP2" },
        { "1:32768,3:100,4:1048576,5:16384", "cURL_Latest" },

        // Java OkHttp library
        { "1:16384,3:4096,4:1048576", "Java_OkHttp" },
        { "1:65535,2:0,3:65535,4:65535", "Java_HTTP2_Client" },

        // .NET HttpClient with HTTP/2
        { "1:65536,3:100,4:1048576", "DotNet_HttpClient" },
        { "2:0,3:100,4:1048576,6:65536", "DotNet_HTTP2" },

        // Rust reqwest library
        { "1:65536,2:0,3:100,4:6291456,5:16384,6:262144", "Rust_Reqwest" },

        // Scrapy with HTTP/2
        { "3:100,4:65536,5:16384", "Scrapy_HTTP2" },

        // Selenium/Puppeteer (headless Chrome)
        { "1:65536,2:0,3:1000,4:6291456,6:262144", "Headless_Chrome_Potential" },
        // Note: Same as Chrome, requires cross-checking with other signals

        // Custom/Unknown stacks (suspicious patterns)
        { "1:1,2:1,3:1,4:1", "Custom_Minimal_Stack" },
        { "4:1", "Bare_Minimum_Settings" }
    };

    private readonly ILogger<Http2FingerprintContributor> _logger;

    public Http2FingerprintContributor(
        ILogger<Http2FingerprintContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "Http2Fingerprint";
    public override int Priority => Manifest?.Priority ?? 10;

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private double Http1PenaltyConfidence => GetParam("http1_penalty_confidence", 0.1);
    private double BotFingerprintConfidence => GetParam("bot_fingerprint_confidence", 0.7);
    private double BrowserFingerprintConfidence => GetParam("browser_fingerprint_confidence", -0.2);
    private double NonStandardPseudoheaderConfidence => GetParam("non_standard_pseudoheader_confidence", 0.3);
    private double NoPriorityConfidence => GetParam("no_priority_confidence", 0.1);
    private double NoWindowUpdatesConfidence => GetParam("no_window_updates_confidence", 0.15);
    private double PushDisabledConfidence => GetParam("push_disabled_confidence", 0.12);
    private double InvalidPrefaceConfidence => GetParam("invalid_preface_confidence", 0.8);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            var protocol = state.HttpContext.Request.Protocol;
            signals.Add(SignalKeys.H2Protocol, protocol);

            // Check if HTTP/2 is being used
            var isHttp2 = protocol.Equals("HTTP/2", StringComparison.OrdinalIgnoreCase) ||
                          protocol.Equals("HTTP/2.0", StringComparison.OrdinalIgnoreCase);

            signals.Add("h2.is_http2", isHttp2);

            if (!isHttp2)
            {
                // HTTP/3 connections are handled by Http3FingerprintContributor — skip with neutral signal
                if (protocol.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
                {
                    signals.Add("h2.is_http3", true);
                    contributions.Add(DetectionContribution.Info(Name, "HTTP/2",
                        "Connection uses HTTP/3 (analyzed by Http3FingerprintContributor)")
                        with { Signals = signals.ToImmutable() });
                    return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
                }

                // HTTP/1.x usage could be legitimate or suspicious depending on context
                // Modern browsers support HTTP/2, but some automation tools don't
                if (protocol.StartsWith("HTTP/1"))
                    contributions.Add(BotContribution(
                        "HTTP/2",
                        $"Using {protocol} instead of HTTP/2 (common for bots)",
                        confidenceOverride: Http1PenaltyConfidence,
                        weightMultiplier: 0.5) with { Signals = signals.ToImmutable() });

                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            // HTTP/2 SETTINGS fingerprinting (requires reverse proxy to capture and forward)
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP2-Settings", out var settingsHeader))
            {
                var settings = settingsHeader.ToString();
                signals.Add("h2.settings_fingerprint", settings);

                // Match against known fingerprints
                var matchedClient = MatchFingerprint(settings);
                if (matchedClient != null)
                {
                    signals.Add(SignalKeys.H2ClientType, matchedClient);

                    if (matchedClient.Contains("Bot") || matchedClient.Contains("HTTP2_Client"))
                        contributions.Add(BotContribution(
                            "HTTP/2",
                            $"HTTP/2 fingerprint matches known automation client: {matchedClient}",
                            confidenceOverride: BotFingerprintConfidence,
                            weightMultiplier: 1.6,
                            botType: BotType.Scraper.ToString()));
                    else
                        // Known browser
                        contributions.Add(HumanContribution(
                            "HTTP/2",
                            $"HTTP/2 fingerprint matches browser: {matchedClient}")
                            with
                            {
                                ConfidenceDelta = BrowserFingerprintConfidence,
                                Weight = WeightHumanSignal * 1.4,
                                Signals = signals.ToImmutable()
                            });
                }
                else
                {
                    // Unknown fingerprint
                    signals.Add("h2.fingerprint_unknown", true);
                }
            }

            // Analyze pseudoheader order (:method, :path, :scheme, :authority)
            // Browsers have consistent ordering, bots may vary
            var pseudoHeaderOrder = ExtractPseudoHeaderOrder(state.HttpContext);
            if (!string.IsNullOrEmpty(pseudoHeaderOrder))
            {
                signals.Add("h2.pseudoheader_order", pseudoHeaderOrder);

                // Standard browser order: method,path,authority,scheme or method,path,scheme,authority
                if (pseudoHeaderOrder != "method,path,authority,scheme" &&
                    pseudoHeaderOrder != "method,path,scheme,authority" &&
                    pseudoHeaderOrder != "method,scheme,authority,path")
                    contributions.Add(BotContribution(
                        "HTTP/2",
                        $"Non-standard HTTP/2 pseudoheader order: {pseudoHeaderOrder}",
                        confidenceOverride: NonStandardPseudoheaderConfidence,
                        weightMultiplier: 1.2) with { Signals = signals.ToImmutable() });
            }

            // Check for HTTP/2 stream priority usage
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP2-Stream-Priority", out var priority))
            {
                signals.Add("h2.stream_priority", priority.ToString());
                signals.Add("h2.uses_priority", true);
            }
            else
            {
                signals.Add("h2.uses_priority", false);
                // Lack of priority is slightly suspicious - browsers use it
                contributions.Add(BotContribution(
                    "HTTP/2",
                    "No HTTP/2 stream priority (browsers typically use this)",
                    confidenceOverride: NoPriorityConfidence,
                    weightMultiplier: 0.6) with { Signals = signals.ToImmutable() });
            }

            // Check for WINDOW_UPDATE behavior patterns
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP2-Window-Updates", out var windowUpdates))
            {
                var updates = windowUpdates.ToString();
                signals.Add("h2.window_update_pattern", updates);

                // Analyze window update frequency and sizes
                if (int.TryParse(updates, out var updateCount))
                {
                    signals.Add("h2.window_update_count", updateCount);

                    if (updateCount == 0)
                        // No window updates is unusual for browsers
                        contributions.Add(BotContribution(
                            "HTTP/2",
                            "No HTTP/2 WINDOW_UPDATE frames (unusual for browsers)",
                            confidenceOverride: NoWindowUpdatesConfidence,
                            weightMultiplier: 0.8) with { Signals = signals.ToImmutable() });
                }
            }

            // Check for HTTP/2 Push support/usage
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP2-Push-Enabled", out var pushEnabled))
            {
                var supportsPush = pushEnabled == "1";
                signals.Add("h2.push_enabled", supportsPush);

                if (!supportsPush)
                    // Many bots disable push
                    contributions.Add(BotContribution(
                        "HTTP/2",
                        "HTTP/2 Server Push disabled (common for bots)",
                        confidenceOverride: PushDisabledConfidence,
                        weightMultiplier: 0.7) with { Signals = signals.ToImmutable() });
            }

            // Analyze connection preface
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP2-Preface-Valid", out var prefaceValid))
            {
                var valid = prefaceValid == "1";
                signals.Add("h2.preface_valid", valid);

                if (!valid)
                    // Invalid preface = definitely suspicious
                    contributions.Add(BotContribution(
                        "HTTP/2",
                        "Invalid HTTP/2 connection preface",
                        confidenceOverride: InvalidPrefaceConfidence,
                        weightMultiplier: 1.8,
                        botType: BotType.Scraper.ToString()));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing HTTP/2 fingerprint");
            signals.Add("h2.analysis_error", ex.Message);
        }

        // If no contributions yet, add neutral with signals
        if (contributions.Count == 0)
        {
            contributions.Add(DetectionContribution.Info(
                Name,
                "HTTP/2",
                "HTTP/2 analysis complete (no anomalies detected)") with { Signals = signals.ToImmutable() });
        }
        else
        {
            // Ensure last contribution has all signals
            if (contributions.Count > 0)
            {
                var last = contributions[^1];
                contributions[^1] = last with { Signals = signals.ToImmutable() };
            }
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private string? MatchFingerprint(string settings)
    {
        foreach (var (fingerprint, client) in KnownFingerprints)
            if (settings.Contains(fingerprint, StringComparison.OrdinalIgnoreCase))
                return client;

        return null;
    }

    private string ExtractPseudoHeaderOrder(HttpContext context)
    {
        // In HTTP/2, pseudoheaders start with ":"
        // Note: ASP.NET Core doesn't expose raw HTTP/2 frames directly
        // This would need to be captured by reverse proxy and passed via header

        if (context.Request.Headers.TryGetValue("X-HTTP2-Pseudoheader-Order", out var order)) return order.ToString();

        // Fallback: infer from standard headers presence
        var parts = new List<string>();
        if (context.Request.Method != null) parts.Add("method");
        if (context.Request.Path.HasValue) parts.Add("path");
        if (context.Request.Scheme != null) parts.Add("scheme");
        if (context.Request.Host.HasValue) parts.Add("authority");

        return string.Join(",", parts);
    }
}