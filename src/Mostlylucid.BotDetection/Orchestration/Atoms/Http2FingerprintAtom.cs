using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) using AKAMAI-style HTTP/2 fingerprinting:
///     SETTINGS frame shape, pseudoheader order, stream priority behaviour,
///     WINDOW_UPDATE cadence, PUSH support, and connection preface validity.
///     Priority 13 -- Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>Http2FingerprintContributor</c>. Behind-proxy detection via
///         X-HTTP-Protocol / trust-gated edge headers preserved.
///     </para>
///     <para>
///         KnownFingerprints catalog is carried over verbatim from the
///         contributor -- catalog cleanup to YAML per feedback_no_word_lists
///         is a separate task.
///     </para>
/// </remarks>
public sealed class Http2FingerprintAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists.
    private static readonly (string Fingerprint, string ClientName)[] KnownFingerprints =
    [
        ("1:65536,2:0,3:1000,4:6291456,6:262144", "Chrome_Desktop_90+"),
        ("1:65536,2:0,3:100,4:6291456,6:262144", "Chrome_Mobile"),
        ("1:262144,2:0,3:100,4:6291456,6:262144", "Chrome_Desktop_Latest"),
        ("1:65536,2:0,3:100,4:131072,5:16384", "Firefox_Desktop"),
        ("1:65536,2:0,3:250,4:131072,5:16384", "Firefox_Latest"),
        ("1:131072,3:100,4:131072,5:16384", "Firefox_Android"),
        ("1:32768,2:0,3:100,4:2097152", "Safari_Desktop"),
        ("2:0,3:100,4:2097152,8:1", "Safari_iOS"),
        ("1:65536,3:100,4:2097152", "Safari_Latest"),
        ("1:65536,2:0,3:1000,4:6291456,6:262144", "Edge_Chromium"),
        ("1:65536,2:0,3:1000,4:6291456,6:262144,8:1", "Opera_Chromium"),
        ("3:100,4:65536", "Go_HTTP2_Client"),
        ("3:1000,4:1048576", "Go_HTTP2_Custom"),
        ("4:4194304", "Go_Minimal"),
        ("3:100,4:6291456", "Python_httpx"),
        ("1:65535,3:100,4:65535", "Python_h2_Library"),
        ("2:0,3:100,4:1048576", "Python_Custom_Stack"),
        ("1:4096,3:100,4:65536", "Node_HTTP2_Bot"),
        ("3:100,4:16777215", "Node_HTTP2_Default"),
        ("1:65535,2:1,3:100,4:65535", "Node_HTTP2_Custom"),
        ("3:100,5:16384", "cURL_HTTP2"),
        ("1:32768,3:100,4:1048576,5:16384", "cURL_Latest"),
        ("1:16384,3:4096,4:1048576", "Java_OkHttp"),
        ("1:65535,2:0,3:65535,4:65535", "Java_HTTP2_Client"),
        ("1:65536,3:100,4:1048576", "DotNet_HttpClient"),
        ("2:0,3:100,4:1048576,6:65536", "DotNet_HTTP2"),
        ("1:65536,2:0,3:100,4:6291456,5:16384,6:262144", "Rust_Reqwest"),
        ("3:100,4:65536,5:16384", "Scrapy_HTTP2"),
        ("1:65536,2:0,3:1000,4:6291456,6:262144", "Headless_Chrome_Potential"),
        ("1:1,2:1,3:1,4:1", "Custom_Minimal_Stack"),
        ("4:1", "Bare_Minimum_Settings")
    ];

    private readonly ILogger<Http2FingerprintAtom> _logger;
    private readonly DeploymentNormTracker _norms;
    private readonly ITransportHeaderTrust? _transportTrust;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private readonly int _populationMinSamples;
    private readonly double _populationRateThreshold;
    private readonly double _http1PenaltyConfidence;
    private readonly double _botFingerprintConfidence;
    private readonly double _browserFingerprintConfidence;
    private readonly double _nonStandardPseudoheaderConfidence;
    private readonly double _noPriorityConfidence;
    private readonly double _noWindowUpdatesConfidence;
    private readonly double _pushDisabledConfidence;
    private readonly double _invalidPrefaceConfidence;

    public Http2FingerprintAtom(
        ILogger<Http2FingerprintAtom> logger,
        IDetectorConfigProvider configProvider,
        DeploymentNormTracker norms,
        IHttpContextAccessor httpContextAccessor,
        ITransportHeaderTrust? transportTrust = null)
        : base(name: "Http2Fingerprint", category: "HTTP/2")
    {
        _logger = logger;
        _norms = norms;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _transportTrust = transportTrust;
        _populationMinSamples = configProvider.GetParameter(Name, "population_min_samples", 20);
        _populationRateThreshold = configProvider.GetParameter(Name, "population_rate_threshold", 0.7);
        _http1PenaltyConfidence = configProvider.GetParameter(Name, "http1_penalty_confidence", 0.1);
        _botFingerprintConfidence = configProvider.GetParameter(Name, "bot_fingerprint_confidence", 0.7);
        _browserFingerprintConfidence = configProvider.GetParameter(Name, "browser_fingerprint_confidence", -0.2);
        _nonStandardPseudoheaderConfidence = configProvider.GetParameter(Name, "non_standard_pseudoheader_confidence", 0.3);
        _noPriorityConfidence = configProvider.GetParameter(Name, "no_priority_confidence", 0.1);
        _noWindowUpdatesConfidence = configProvider.GetParameter(Name, "no_window_updates_confidence", 0.15);
        _pushDisabledConfidence = configProvider.GetParameter(Name, "push_disabled_confidence", 0.12);
        _invalidPrefaceConfidence = configProvider.GetParameter(Name, "invalid_preface_confidence", 0.8);
    }

    public override int Priority => 13;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double SpoofedEdgeHeadersConfidence => _configProvider.GetParameter(Name, "spoofed_edge_headers_confidence", 0.3);
    private double SpoofedEdgeHeadersWeight => _configProvider.GetParameter(Name, "spoofed_edge_headers_weight", 1.2);
    private double WeightHumanSignal => _configProvider.GetDefaults(Name).Weights.HumanSignal;

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        sink.Raise("h2.ran", sessionId);

        var contributions = new List<DetectionContribution>();
        var req = context.Request;

        try
        {
            var trustHeaders = true;
            if (_transportTrust is not null)
            {
                var stateShim = new Mostlylucid.BotDetection.Orchestration.BlackboardState
                {
                    HttpContext = context,
                    Signals = new Dictionary<string, object>(),
                    CompletedDetectors = new HashSet<string>(),
                    FailedDetectors = new HashSet<string>(),
                    Contributions = Array.Empty<DetectionContribution>(),
                    RequestId = sessionId
                };
                var trust = _transportTrust.Evaluate(stateShim);
                trustHeaders = trust.Trusted;

                if (!trust.Trusted)
                {
                    var gatedHeaderPresent = req.Headers.ContainsKey("X-HTTP-Protocol")
                        || req.Headers.ContainsKey("X-HTTP2-Settings")
                        || req.Headers.ContainsKey("X-HTTP2-Stream-Priority")
                        || req.Headers.ContainsKey("X-HTTP2-Window-Updates")
                        || req.Headers.ContainsKey("X-HTTP2-Push-Enabled")
                        || req.Headers.ContainsKey("X-HTTP2-Preface-Valid")
                        || req.Headers.ContainsKey("X-HTTP2-Pseudoheader-Order");

                    if (gatedHeaderPresent)
                    {
                        sink.Raise($"{SignalKeys.TransportSpoofedEdgeHeaders}:true", sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = SpoofedEdgeHeadersConfidence,
                            Weight = SpoofedEdgeHeadersWeight,
                            Reason = "Edge HTTP/2 fingerprint headers from an untrusted direct peer (possible spoof)",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                }
            }

            var rawProtocol = req.Protocol;
            var protocol = rawProtocol;
            var behindProxy = false;

            if (trustHeaders
                && req.Headers.TryGetValue("X-HTTP-Protocol", out var originalProtocol)
                && !string.IsNullOrEmpty(originalProtocol.ToString()))
            {
                protocol = originalProtocol.ToString();
                behindProxy = true;
            }

            sink.Raise($"{SignalKeys.H2Protocol}:{protocol}", sessionId);
            sink.Raise($"{SignalKeys.H2BehindProxy}:{(behindProxy ? "true" : "false")}", sessionId);

            var isHttp2 = protocol.Equals("HTTP/2", StringComparison.OrdinalIgnoreCase)
                          || protocol.Equals("HTTP/2.0", StringComparison.OrdinalIgnoreCase);
            sink.Raise($"{SignalKeys.H2IsHttp2}:{(isHttp2 ? "true" : "false")}", sessionId);

            if (!isHttp2)
            {
                if (protocol.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
                {
                    sink.Raise($"{SignalKeys.H2ObservedHttp3}:true", sessionId);
                    contributions.Add(DetectionContribution.Info(Name, Category,
                        "Connection uses HTTP/3 (analyzed by Http3FingerprintAtom)"));
                    return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
                }

                if (protocol.StartsWith("HTTP/1"))
                {
                    var uaFamily = sink.ReadHint(SignalKeys.UserAgentFamily) ?? "unknown";
                    var eval = _norms.Evaluate(
                        DeploymentNormTracker.Features.Http2, uaFamily, present: false,
                        _populationMinSamples, _populationRateThreshold,
                        out var http2Rate, out var samples);

                    sink.Raise($"{SignalKeys.H2PopulationHttp2Rate}:{http2Rate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
                    sink.Raise($"{SignalKeys.H2PopulationSamples}:{samples}", sessionId);

                    contributions.Add(eval switch
                    {
                        NormEvaluation.WarmingUp => DetectionContribution.Info(Name, Category,
                            $"Using {protocol}; deployment still calibrating ({_norms.TotalRequests} requests seen)"),
                        NormEvaluation.BelowNorm => DetectionContribution.Info(Name, Category,
                            $"Using {protocol}; environment norm is HTTP/1.1 ({http2Rate:P0} HTTP/2 over {samples} samples)"),
                        _ => new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = samples < _populationMinSamples
                                ? _http1PenaltyConfidence * 0.5
                                : _http1PenaltyConfidence * http2Rate,
                            Weight = 0.5,
                            Reason = $"Using {protocol} instead of HTTP/2 (HTTP/2 rate: {http2Rate:P0} over {samples} samples)",
                            BotType = BotType.Unknown.ToString()
                        }
                    });
                }

                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
            }

            {
                var uaFamily = sink.ReadHint(SignalKeys.UserAgentFamily) ?? "unknown";
                _norms.Record(DeploymentNormTracker.Features.Http2, uaFamily, present: true);
            }

            if (trustHeaders && req.Headers.TryGetValue("X-HTTP2-Settings", out var settingsHeader))
            {
                var settings = settingsHeader.ToString();
                sink.Raise($"h2.settings_fingerprint:{settings}", sessionId);
                var matchedClient = MatchFingerprint(settings);
                if (matchedClient is not null)
                {
                    sink.Raise($"{SignalKeys.H2ClientType}:{matchedClient}", sessionId);
                    if (matchedClient.Contains("Bot") || matchedClient.Contains("HTTP2_Client"))
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = _botFingerprintConfidence,
                            Weight = 1.6,
                            Reason = $"HTTP/2 fingerprint matches known automation client: {matchedClient}",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                    else
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = _browserFingerprintConfidence,
                            Weight = WeightHumanSignal * 1.4,
                            Reason = $"HTTP/2 fingerprint matches browser: {matchedClient}"
                        });
                    }
                }
                else
                {
                    sink.Raise("h2.fingerprint_unknown:true", sessionId);
                }
            }

            var pseudoHeaderOrder = ExtractPseudoHeaderOrder(context, trustHeaders);
            if (!string.IsNullOrEmpty(pseudoHeaderOrder))
            {
                sink.Raise($"h2.pseudoheader_order:{pseudoHeaderOrder}", sessionId);
                if (pseudoHeaderOrder != "method,path,authority,scheme"
                    && pseudoHeaderOrder != "method,path,scheme,authority"
                    && pseudoHeaderOrder != "method,scheme,authority,path")
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = _nonStandardPseudoheaderConfidence,
                        Weight = 1.2,
                        Reason = $"Non-standard HTTP/2 pseudoheader order: {pseudoHeaderOrder}",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            if (trustHeaders)
            {
                var priorityUaFamily = sink.ReadHint(SignalKeys.UserAgentFamily) ?? "unknown";
                var hasPriority = req.Headers.TryGetValue("X-HTTP2-Stream-Priority", out var priority);
                if (hasPriority)
                {
                    sink.Raise($"h2.stream_priority:{priority}", sessionId);
                    sink.Raise("h2.uses_priority:true", sessionId);
                }
                else
                {
                    sink.Raise("h2.uses_priority:false", sessionId);
                }

                var priorityEval = _norms.Evaluate(
                    DeploymentNormTracker.Features.Http2StreamPriority, priorityUaFamily, present: hasPriority,
                    _populationMinSamples, _populationRateThreshold,
                    out _, out var prioritySamples);

                if (!hasPriority && priorityEval == NormEvaluation.AboveNorm)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = prioritySamples < _populationMinSamples
                            ? _noPriorityConfidence * 0.5
                            : _noPriorityConfidence,
                        Weight = 0.6,
                        Reason = "No HTTP/2 stream priority (browsers typically use this)",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-HTTP2-Window-Updates", out var windowUpdates))
            {
                var updates = windowUpdates.ToString();
                sink.Raise($"h2.window_update_pattern:{updates}", sessionId);
                if (int.TryParse(updates, out var updateCount))
                {
                    sink.Raise($"h2.window_update_count:{updateCount}", sessionId);
                    if (updateCount == 0)
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = _noWindowUpdatesConfidence,
                            Weight = 0.8,
                            Reason = "No HTTP/2 WINDOW_UPDATE frames (unusual for browsers)",
                            BotType = BotType.Unknown.ToString()
                        });
                    }
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-HTTP2-Push-Enabled", out var pushEnabled))
            {
                var supportsPush = pushEnabled == "1";
                sink.Raise($"h2.push_enabled:{(supportsPush ? "true" : "false")}", sessionId);
                if (!supportsPush)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = _pushDisabledConfidence,
                        Weight = 0.7,
                        Reason = "HTTP/2 Server Push disabled (common for bots)",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-HTTP2-Preface-Valid", out var prefaceValid))
            {
                var valid = prefaceValid == "1";
                sink.Raise($"h2.preface_valid:{(valid ? "true" : "false")}", sessionId);
                if (!valid)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = _invalidPrefaceConfidence,
                        Weight = 1.8,
                        Reason = "Invalid HTTP/2 connection preface",
                        BotType = BotType.Scraper.ToString()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing HTTP/2 fingerprint");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "HTTP/2 analysis complete (no anomalies detected)"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static string? MatchFingerprint(string settings)
    {
        foreach (var (fingerprint, client) in KnownFingerprints)
        {
            if (settings.Contains(fingerprint, StringComparison.OrdinalIgnoreCase))
                return client;
        }
        return null;
    }

    private static string ExtractPseudoHeaderOrder(HttpContext context, bool trustHeaders)
    {
        if (trustHeaders && context.Request.Headers.TryGetValue("X-HTTP2-Pseudoheader-Order", out var order))
            return order.ToString();

        var parts = new List<string>();
        if (context.Request.Method != null) parts.Add("method");
        if (context.Request.Path.HasValue) parts.Add("path");
        if (context.Request.Scheme != null) parts.Add("scheme");
        if (context.Request.Host.HasValue) parts.Add("authority");
        return string.Join(",", parts);
    }
}
