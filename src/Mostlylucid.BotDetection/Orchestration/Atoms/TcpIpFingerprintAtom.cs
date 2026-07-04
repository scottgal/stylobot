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
///     SensorAtom (per Taxonomy.md) doing passive TCP/IP fingerprinting
///     from edge-injected headers: TCP window size, TTL, options, MSS, IP
///     DF flag, IP ID pattern. Priority 11 -- Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>TcpIpFingerprintContributor</c>.
///     </para>
///     <para>
///         WindowSizePatterns + TtlPatterns catalogs carried over verbatim
///         from the contributor. TODO: migrate to YAML per
///         feedback_no_word_lists.
///     </para>
///     <para>
///         Signal Assay applied to Connection-header absence -- calibrated
///         against DeploymentNormTracker so real browsers behind a tunnel
///         that strips the header don't get penalised.
///     </para>
/// </remarks>
public sealed class TcpIpFingerprintAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists.
    private static readonly (int WindowSize, string[] Patterns)[] WindowSizePatterns =
    [
        (8192, ["Windows", "Windows_95/98/ME"]),
        (16384, ["Windows", "Windows_2000/NT4"]),
        (64240, ["Windows", "Windows_XP_SP1"]),
        (65535, ["Windows", "Windows_XP_SP2+/Vista/7/8/10/11"]),
        (64512, ["Windows", "Windows_Server_2008+"]),
        (5840, ["Linux", "Linux_2.2.x"]),
        (5792, ["Linux", "Linux_2.4.x"]),
        (14600, ["Linux", "Linux_2.6.x_early"]),
        (29200, ["Linux", "Linux_2.6.x_later"]),
        (14480, ["Linux", "Linux_3.x/4.x/5.x"]),
        (65535, ["MacOS", "MacOS_X", "iOS"]),
        (131072, ["MacOS", "MacOS_Recent"]),
        (28960, ["Android", "Android_4.x"]),
        (14600, ["Android", "Android_5.x+"]),
        (65535, ["FreeBSD", "OpenBSD", "NetBSD"]),
        (32768, ["FreeBSD", "FreeBSD_Old"]),
        (49152, ["Solaris", "Solaris_10+"]),
        (49640, ["Solaris", "Solaris_11"]),
        (4096, ["Bot", "Go_net/http", "Custom_Stack"]),
        (65536, ["Bot", "Go_HTTP_Client_Custom"]),
        (32768, ["Bot", "Python_requests", "Python_urllib"]),
        (87380, ["Bot", "Python_Default_Stack"]),
        (32768, ["Bot", "cURL", "libcurl"]),
        (16384, ["Bot", "cURL_Old"]),
        (65535, ["Bot", "Java_HttpClient"]),
        (8192, ["Bot", "Java_Old_Stack"]),
        (65535, ["Bot", "DotNet_HttpClient"]),
        (64240, ["Bot", "DotNet_Framework"]),
        (65535, ["Bot", "Node_HTTP_Module"]),
        (65535, ["Bot", "Scrapy", "Twisted_Framework"]),
        (1024, ["Bot", "Tiny_Window_Suspicious"]),
        (512, ["Bot", "Very_Small_Window_Bot"]),
        (1, ["Bot", "Minimal_Stack_Definite_Bot"])
    ];

    private static readonly (int Ttl, string[] Patterns)[] TtlPatterns =
    [
        (64, ["Linux", "Unix", "MacOS", "Android", "iOS"]),
        (63, ["Linux", "1_Hop_Away"]),
        (62, ["Linux", "2_Hops_Away"]),
        (61, ["Linux", "3_Hops_Away"]),
        (128, ["Windows", "Windows_All_Versions"]),
        (127, ["Windows", "1_Hop_Away"]),
        (126, ["Windows", "2_Hops_Away"]),
        (125, ["Windows", "3_Hops_Away"]),
        (255, ["Network_Device", "Cisco", "Router", "Firewall"]),
        (254, ["Network_Device", "1_Hop_Away"]),
        (32, ["Windows", "Windows_95/98/ME"]),
        (60, ["MacOS", "MacOS_Classic"]),
        (1, ["Bot", "Extremely_Suspicious_TTL"]),
        (2, ["Bot", "Very_Low_TTL"]),
        (10, ["Bot", "Unusually_Low_TTL"]),
        (30, ["Bot", "Non_Standard_TTL"]),
        (100, ["Bot", "Unusual_TTL_100"]),
        (200, ["Bot", "Unusual_TTL_200"])
    ];

    private readonly ILogger<TcpIpFingerprintAtom> _logger;
    private readonly ITransportHeaderTrust? _transportTrust;
    private readonly DeploymentNormTracker? _norms;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly int _populationMinSamples;
    private readonly double _populationRateThreshold;

    public TcpIpFingerprintAtom(
        ILogger<TcpIpFingerprintAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        ITransportHeaderTrust? transportTrust = null,
        DeploymentNormTracker? norms = null)
        : base(name: "TcpIpFingerprint", category: "TCP/IP")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _transportTrust = transportTrust;
        _norms = norms;
        _populationMinSamples = configProvider.GetParameter(Name, "population_min_samples", 20);
        _populationRateThreshold = configProvider.GetParameter(Name, "population_rate_threshold", 0.7);
    }

    public override int Priority => 11;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double WindowBotConfidence => _configProvider.GetParameter(Name, "window_bot_confidence", 0.55);
    private double WindowBotWeight => _configProvider.GetParameter(Name, "window_bot_weight", 1.3);
    private double WindowUnusualConfidence => _configProvider.GetParameter(Name, "window_unusual_confidence", 0.25);
    private double WindowUnusualWeight => _configProvider.GetParameter(Name, "window_unusual_weight", 1.1);
    private double TtlBotConfidence => _configProvider.GetParameter(Name, "ttl_bot_confidence", 0.6);
    private double TtlBotWeight => _configProvider.GetParameter(Name, "ttl_bot_weight", 1.4);
    private double TtlUnusualConfidence => _configProvider.GetParameter(Name, "ttl_unusual_confidence", 0.3);
    private double TtlUnusualWeight => _configProvider.GetParameter(Name, "ttl_unusual_weight", 1.2);
    private double TcpOptionsMissingModernConfidence => _configProvider.GetParameter(Name, "tcp_options_missing_modern_confidence", 0.2);
    private double TcpOptionsMissingModernWeight => _configProvider.GetParameter(Name, "tcp_options_missing_modern_weight", 0.9);
    private double TcpOptionsMinimalConfidence => _configProvider.GetParameter(Name, "tcp_options_minimal_confidence", 0.25);
    private double TcpOptionsMinimalWeight => _configProvider.GetParameter(Name, "tcp_options_minimal_weight", 1.0);
    private double MssOldDefaultConfidence => _configProvider.GetParameter(Name, "mss_old_default_confidence", 0.3);
    private double MssOldDefaultWeight => _configProvider.GetParameter(Name, "mss_old_default_weight", 1.1);
    private double MssUnusualConfidence => _configProvider.GetParameter(Name, "mss_unusual_confidence", 0.15);
    private double MssUnusualWeight => _configProvider.GetParameter(Name, "mss_unusual_weight", 0.8);
    private double IpNoDfConfidence => _configProvider.GetParameter(Name, "ip_no_df_confidence", 0.15);
    private double IpNoDfWeight => _configProvider.GetParameter(Name, "ip_no_df_weight", 0.8);
    private double ConnectionMissingConfidence => _configProvider.GetParameter(Name, "connection_missing_confidence", 0.2);
    private double ConnectionMissingWeight => _configProvider.GetParameter(Name, "connection_missing_weight", 0.7);
    private double ConnectionCloseConfidence => _configProvider.GetParameter(Name, "connection_close_confidence", 0.1);
    private double ConnectionCloseWeight => _configProvider.GetParameter(Name, "connection_close_weight", 0.6);
    private double SpoofedEdgeHeadersConfidence => _configProvider.GetParameter(Name, "spoofed_edge_headers_confidence", 0.3);
    private double SpoofedEdgeHeadersWeight => _configProvider.GetParameter(Name, "spoofed_edge_headers_weight", 1.2);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        var contributions = new List<DetectionContribution>();
        var req = context.Request;

        try
        {
            var trustHeaders = true;
            if (_transportTrust is not null)
            {
                var trust = _transportTrust.Evaluate(context, sink, sessionId);
                trustHeaders = trust.Trusted;

                if (!trust.Trusted)
                {
                    var gatedHeaderPresent = req.Headers.ContainsKey("X-TCP-Window")
                        || req.Headers.ContainsKey("X-TCP-TTL")
                        || req.Headers.ContainsKey("X-TCP-Options")
                        || req.Headers.ContainsKey("X-TCP-MSS")
                        || req.Headers.ContainsKey("X-IP-DF")
                        || req.Headers.ContainsKey("X-IP-ID-Pattern");

                    if (gatedHeaderPresent)
                    {
                        sink.Raise($"{SignalKeys.TransportSpoofedEdgeHeaders}:true", sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = SpoofedEdgeHeadersConfidence,
                            Weight = SpoofedEdgeHeadersWeight,
                            Reason = "Edge TCP/IP fingerprint headers from an untrusted direct peer (possible spoof)",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-TCP-Window", out var windowHeader)
                && int.TryParse(windowHeader, out var windowSize))
            {
                sink.Raise($"tcp.window_size:{windowSize}", sessionId);
                AnalyzeWindowSize(windowSize, sink, sessionId, contributions);
            }

            if (trustHeaders && req.Headers.TryGetValue("X-TCP-TTL", out var ttlHeader)
                && int.TryParse(ttlHeader, out var ttl))
            {
                sink.Raise($"tcp.ttl:{ttl}", sessionId);
                AnalyzeTtl(ttl, sink, sessionId, contributions);
            }

            if (trustHeaders && req.Headers.TryGetValue("X-TCP-Options", out var tcpOptions))
            {
                var options = tcpOptions.ToString();
                sink.Raise($"tcp.options_pattern:{options}", sessionId);
                AnalyzeTcpOptions(options, sink, sessionId, contributions);
            }

            if (trustHeaders && req.Headers.TryGetValue("X-TCP-MSS", out var mssHeader)
                && int.TryParse(mssHeader, out var mss))
            {
                sink.Raise($"tcp.mss:{mss}", sessionId);
                AnalyzeMss(mss, contributions);
            }

            if (trustHeaders && req.Headers.TryGetValue("X-IP-DF", out var dfFlag))
            {
                var dontFragment = dfFlag == "1";
                sink.Raise($"ip.dont_fragment:{(dontFragment ? "true" : "false")}", sessionId);
                if (!dontFragment)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = IpNoDfConfidence,
                        Weight = IpNoDfWeight,
                        Reason = "Network packet configuration differs from modern browsers"
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-IP-ID-Pattern", out var ipIdPattern))
            {
                sink.Raise($"ip.id_pattern:{ipIdPattern}", sessionId);
                if (ipIdPattern == "sequential")
                    sink.Raise($"{SignalKeys.TcpOsHint}:Windows", sessionId);
                else if (ipIdPattern == "random")
                    sink.Raise($"{SignalKeys.TcpOsHint}:Linux/BSD", sessionId);
            }

            // Connection header + Signal Assay against tunnel-stripped headers.
            var connectionHeader = req.Headers.Connection.ToString();
            sink.Raise($"{SignalKeys.TcpConnectionHeader}:{connectionHeader}", sessionId);

            var hasConnectionHeader = !string.IsNullOrEmpty(connectionHeader);
            var connectionEval = NormEvaluation.BelowNorm;
            if (_norms is not null)
            {
                var connUaFamily = sink.ReadHint(SignalKeys.UserAgentFamily) ?? "unknown";
                connectionEval = _norms.Evaluate(
                    DeploymentNormTracker.Features.TcpConnectionHeader, connUaFamily, present: hasConnectionHeader,
                    _populationMinSamples, _populationRateThreshold,
                    out _, out _);
            }

            if (!hasConnectionHeader)
            {
                if (connectionEval == NormEvaluation.AboveNorm)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = ConnectionMissingConfidence,
                        Weight = ConnectionMissingWeight,
                        Reason = "Missing connection reuse header (unusual for real browsers)"
                    });
                }
            }
            else if (connectionHeader.Equals("close", StringComparison.OrdinalIgnoreCase))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = ConnectionCloseConfidence,
                    Weight = ConnectionCloseWeight,
                    Reason = "Client closes connection after each request (bots often avoid persistent connections)"
                });
            }

            if (trustHeaders && req.Headers.TryGetValue("X-HTTP-Pipelining", out var pipelining))
                sink.Raise($"http.pipelining_supported:{(pipelining == "1" ? "true" : "false")}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing TCP/IP fingerprint");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "Network fingerprint analysis complete (no anomalies detected)"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private void AnalyzeWindowSize(int windowSize, SignalSink sink, string sessionId, List<DetectionContribution> contributions)
    {
        var matches = WindowSizePatterns.Where(p => p.WindowSize == windowSize).ToArray();
        if (matches.Length > 0)
        {
            var allPatterns = matches.SelectMany(m => m.Patterns).ToArray();
            var pattern = allPatterns[0];
            sink.Raise($"{SignalKeys.TcpOsHintWindow}:{pattern}", sessionId);

            if (allPatterns.Any(p => p.Contains("Bot")))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = WindowBotConfidence,
                    Weight = WindowBotWeight,
                    Reason = $"Network buffer size matches a known bot fingerprint ({pattern})",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }
        else if (windowSize < 1024 || windowSize > 65535 || !IsPowerOfTwo(windowSize))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = WindowUnusualConfidence,
                Weight = WindowUnusualWeight,
                Reason = "Unusual network buffer configuration (does not match standard browsers or operating systems)"
            });
        }
    }

    private void AnalyzeTtl(int ttl, SignalSink sink, string sessionId, List<DetectionContribution> contributions)
    {
        var ttlMatches = TtlPatterns.Where(p => p.Ttl == ttl).ToArray();
        if (ttlMatches.Length > 0)
        {
            var allTtlPatterns = ttlMatches.SelectMany(m => m.Patterns).ToArray();
            var pattern = allTtlPatterns[0];
            sink.Raise($"{SignalKeys.TcpOsHintTtl}:{pattern}", sessionId);

            if (allTtlPatterns.Any(p => p.Contains("Bot")))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = TtlBotConfidence,
                    Weight = TtlBotWeight,
                    Reason = "Network hop count matches a known bot fingerprint",
                    BotType = BotType.Scraper.ToString()
                });
            }
        }
        else if (ttl < 30 || ttl > 255 || (ttl != 64 && ttl != 128 && ttl != 255))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = TtlUnusualConfidence,
                Weight = TtlUnusualWeight,
                Reason = "Unusual network hop count (does not match standard browsers or operating systems)"
            });
        }
    }

    private void AnalyzeTcpOptions(string options, SignalSink sink, string sessionId, List<DetectionContribution> contributions)
    {
        var hasTimestamp = options.Contains("TS", StringComparison.OrdinalIgnoreCase);
        var hasSack = options.Contains("SACK", StringComparison.OrdinalIgnoreCase);
        var hasWindowScale = options.Contains("WS", StringComparison.OrdinalIgnoreCase);

        sink.Raise($"tcp.has_timestamp:{(hasTimestamp ? "true" : "false")}", sessionId);
        sink.Raise($"tcp.has_sack:{(hasSack ? "true" : "false")}", sessionId);
        sink.Raise($"tcp.has_window_scale:{(hasWindowScale ? "true" : "false")}", sessionId);

        var modernOptions = hasTimestamp && hasSack && hasWindowScale;
        sink.Raise($"tcp.modern_options:{(modernOptions ? "true" : "false")}", sessionId);

        if (!modernOptions)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = TcpOptionsMissingModernConfidence,
                Weight = TcpOptionsMissingModernWeight,
                Reason = "Missing modern network features that real browsers include"
            });
        }

        if (options.Split(',').Length <= 2)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = TcpOptionsMinimalConfidence,
                Weight = TcpOptionsMinimalWeight,
                Reason = "Very few network options set (typical for automation tools, not real browsers)"
            });
        }
    }

    private void AnalyzeMss(int mss, List<DetectionContribution> contributions)
    {
        if (mss == 536)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MssOldDefaultConfidence,
                Weight = MssOldDefaultWeight,
                Reason = "Minimal network packet size (indicates old or custom networking, not a real browser)"
            });
        }
        else if (mss < 536 || mss > 1460)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MssUnusualConfidence,
                Weight = MssUnusualWeight,
                Reason = "Non-standard network packet size (does not match standard browsers)"
            });
        }
    }

    private static bool IsPowerOfTwo(int n) => (n & (n - 1)) == 0 && n != 0;
}
