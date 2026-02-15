using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     TCP/IP stack fingerprinting contributor.
///     Analyzes TCP/IP layer characteristics to detect OS and identify automation.
///     Best-in-breed approach:
///     - TCP window size analysis
///     - TTL (Time To Live) analysis
///     - TCP options and flags
///     - IP fragmentation patterns
///     - Similar to p0f (passive OS fingerprinting)
///     Raises signals for behavioral waveform correlation:
///     - tcp.window_size
///     - tcp.ttl
///     - tcp.options_pattern
///     - tcp.mss (Maximum Segment Size)
///     - tcp.os_fingerprint
/// </summary>
public class TcpIpFingerprintContributor : ContributingDetectorBase
{
    // Known TCP window sizes for different systems (list allows multiple OS per window size)
    // Based on p0f database and real-world observations
    private static readonly (int WindowSize, string[] Patterns)[] WindowSizePatterns =
    [
        // Windows patterns
        (8192, ["Windows", "Windows_95/98/ME"]),
        (16384, ["Windows", "Windows_2000/NT4"]),
        (64240, ["Windows", "Windows_XP_SP1"]),
        (65535, ["Windows", "Windows_XP_SP2+/Vista/7/8/10/11"]),
        (64512, ["Windows", "Windows_Server_2008+"]),

        // Linux patterns (kernel version dependent)
        (5840, ["Linux", "Linux_2.2.x"]),
        (5792, ["Linux", "Linux_2.4.x"]),
        (14600, ["Linux", "Linux_2.6.x_early"]),
        (29200, ["Linux", "Linux_2.6.x_later"]),
        (14480, ["Linux", "Linux_3.x/4.x/5.x"]),

        // macOS/iOS patterns
        (65535, ["MacOS", "MacOS_X", "iOS"]),
        (131072, ["MacOS", "MacOS_Recent"]),

        // Android patterns
        (28960, ["Android", "Android_4.x"]),
        (14600, ["Android", "Android_5.x+"]),

        // BSD variants
        (65535, ["FreeBSD", "OpenBSD", "NetBSD"]),
        (32768, ["FreeBSD", "FreeBSD_Old"]),

        // Solaris/Unix
        (49152, ["Solaris", "Solaris_10+"]),
        (49640, ["Solaris", "Solaris_11"]),

        // Bot/Automation patterns
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

        // Suspicious/Custom stacks
        (1024, ["Bot", "Tiny_Window_Suspicious"]),
        (512, ["Bot", "Very_Small_Window_Bot"]),
        (1, ["Bot", "Minimal_Stack_Definite_Bot"])
    ];

    // Typical TTL values by OS (list allows multiple OS per TTL)
    // TTL gets decremented by each router hop, so we check initial values
    private static readonly (int Ttl, string[] Patterns)[] TtlPatterns =
    [
        // Linux/Unix (initial TTL 64)
        (64, ["Linux", "Unix", "MacOS", "Android", "iOS"]),
        (63, ["Linux", "1_Hop_Away"]),
        (62, ["Linux", "2_Hops_Away"]),
        (61, ["Linux", "3_Hops_Away"]),

        // Windows (initial TTL 128)
        (128, ["Windows", "Windows_All_Versions"]),
        (127, ["Windows", "1_Hop_Away"]),
        (126, ["Windows", "2_Hops_Away"]),
        (125, ["Windows", "3_Hops_Away"]),

        // Network devices (initial TTL 255)
        (255, ["Network_Device", "Cisco", "Router", "Firewall"]),
        (254, ["Network_Device", "1_Hop_Away"]),

        // Old systems
        (32, ["Windows", "Windows_95/98/ME"]),
        (60, ["MacOS", "MacOS_Classic"]),

        // Suspicious/Bot patterns
        (1, ["Bot", "Extremely_Suspicious_TTL"]),
        (2, ["Bot", "Very_Low_TTL"]),
        (10, ["Bot", "Unusually_Low_TTL"]),
        (30, ["Bot", "Non_Standard_TTL"]),
        (100, ["Bot", "Unusual_TTL_100"]),
        (200, ["Bot", "Unusual_TTL_200"])
    ];

    private readonly ILogger<TcpIpFingerprintContributor> _logger;

    public TcpIpFingerprintContributor(ILogger<TcpIpFingerprintContributor> logger)
    {
        _logger = logger;
    }

    public override string Name => "TcpIpFingerprint";
    public override int Priority => 11; // Run early

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            // Extract TCP/IP characteristics from headers and connection info
            // Note: Most of this requires reverse proxy configuration to pass headers

            // Check for TCP window size (usually passed by reverse proxy as X-TCP-Window)
            if (state.HttpContext.Request.Headers.TryGetValue("X-TCP-Window", out var windowHeader) &&
                int.TryParse(windowHeader, out var windowSize))
            {
                signals.Add("tcp.window_size", windowSize);
                AnalyzeWindowSize(windowSize, contributions, signals);
            }

            // Check for TTL (Time To Live) - passed by reverse proxy as X-TCP-TTL
            if (state.HttpContext.Request.Headers.TryGetValue("X-TCP-TTL", out var ttlHeader) &&
                int.TryParse(ttlHeader, out var ttl))
            {
                signals.Add("tcp.ttl", ttl);
                AnalyzeTtl(ttl, contributions, signals);
            }

            // Check for TCP options fingerprint (passed by reverse proxy as X-TCP-Options)
            if (state.HttpContext.Request.Headers.TryGetValue("X-TCP-Options", out var tcpOptions))
            {
                var options = tcpOptions.ToString();
                signals.Add("tcp.options_pattern", options);
                AnalyzeTcpOptions(options, contributions, signals);
            }

            // Check for MSS (Maximum Segment Size)
            if (state.HttpContext.Request.Headers.TryGetValue("X-TCP-MSS", out var mssHeader) &&
                int.TryParse(mssHeader, out var mss))
            {
                signals.Add("tcp.mss", mss);
                AnalyzeMss(mss, contributions, signals);
            }

            // Analyze IP fragmentation patterns
            if (state.HttpContext.Request.Headers.TryGetValue("X-IP-DF", out var dfFlag))
            {
                var dontFragment = dfFlag == "1";
                signals.Add("ip.dont_fragment", dontFragment);

                // Modern systems set DF flag, old/custom stacks may not
                if (!dontFragment)
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "TCP/IP",
                        ConfidenceDelta = 0.15,
                        Weight = 0.8,
                        Reason = "Network packet configuration differs from modern browsers",
                        Signals = signals.ToImmutable()
                    });
            }

            // Check for IP ID patterns (sequential = Windows, random = Linux/BSD)
            if (state.HttpContext.Request.Headers.TryGetValue("X-IP-ID-Pattern", out var ipIdPattern))
            {
                signals.Add("ip.id_pattern", ipIdPattern.ToString());

                if (ipIdPattern == "sequential")
                    signals.Add(SignalKeys.TcpOsHint, "Windows");
                else if (ipIdPattern == "random") signals.Add(SignalKeys.TcpOsHint, "Linux/BSD");
            }

            // Analyze connection reuse patterns
            var connectionHeader = state.HttpContext.Request.Headers.Connection.ToString();
            signals.Add("tcp.connection_header", connectionHeader);

            if (string.IsNullOrEmpty(connectionHeader))
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "TCP/IP",
                    ConfidenceDelta = 0.2,
                    Weight = 0.7,
                    Reason = "Missing connection reuse header (unusual for real browsers)",
                    Signals = signals.ToImmutable()
                });
            else if (connectionHeader.Equals("close", StringComparison.OrdinalIgnoreCase))
                // Bots often use Connection: close to avoid keep-alive
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "TCP/IP",
                    ConfidenceDelta = 0.1,
                    Weight = 0.6,
                    Reason = "Client closes connection after each request (bots often avoid persistent connections)",
                    Signals = signals.ToImmutable()
                });

            // Check for pipelining support (modern feature)
            if (state.HttpContext.Request.Headers.TryGetValue("X-HTTP-Pipelining", out var pipelining))
                signals.Add("http.pipelining_supported", pipelining == "1");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing TCP/IP fingerprint");
            signals.Add("tcp.analysis_error", ex.Message);
        }

        // If no specific indicators found, add neutral contribution with signals
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "TCP/IP",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Network fingerprint analysis complete (no anomalies detected)",
                Signals = signals.ToImmutable()
            });
        }
        else
        {
            // Update last contribution to include all signals
            if (contributions.Count > 0)
            {
                var last = contributions[^1];
                contributions[^1] = last with { Signals = signals.ToImmutable() };
            }
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private void AnalyzeWindowSize(int windowSize, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        // Find all matching entries (multiple OS/bot patterns can share the same window size)
        var matches = WindowSizePatterns.Where(p => p.WindowSize == windowSize).ToArray();
        if (matches.Length > 0)
        {
            var allPatterns = matches.SelectMany(m => m.Patterns).ToArray();
            var pattern = allPatterns[0];
            signals.Add(SignalKeys.TcpOsHintWindow, pattern);

            if (allPatterns.Any(p => p.Contains("Bot")))
                contributions.Add(DetectionContribution.Bot(
                    Name, "TCP/IP", 0.55,
                    $"Network buffer size matches a known bot fingerprint ({pattern})",
                    weight: 1.3,
                    botType: BotType.Scraper.ToString()));
        }
        else
        {
            // Unusual window size
            if (windowSize < 1024 || windowSize > 65535 || !IsPowerOfTwo(windowSize))
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "TCP/IP",
                    ConfidenceDelta = 0.25,
                    Weight = 1.1,
                    Reason = "Unusual network buffer configuration (does not match standard browsers or operating systems)",
                    Signals = signals.ToImmutable()
                });
        }
    }

    private void AnalyzeTtl(int ttl, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        var ttlMatches = TtlPatterns.Where(p => p.Ttl == ttl).ToArray();
        if (ttlMatches.Length > 0)
        {
            var allTtlPatterns = ttlMatches.SelectMany(m => m.Patterns).ToArray();
            var pattern = allTtlPatterns[0];
            signals.Add(SignalKeys.TcpOsHintTtl, pattern);

            if (allTtlPatterns.Any(p => p.Contains("Bot")))
                contributions.Add(DetectionContribution.Bot(
                    Name, "TCP/IP", 0.6,
                    "Network hop count matches a known bot fingerprint",
                    weight: 1.4,
                    botType: BotType.Scraper.ToString()));
        }
        else
        {
            // TTL not matching standard patterns
            if (ttl < 30 || ttl > 255 || (ttl != 64 && ttl != 128 && ttl != 255))
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "TCP/IP",
                    ConfidenceDelta = 0.3,
                    Weight = 1.2,
                    Reason = "Unusual network hop count (does not match standard browsers or operating systems)",
                    Signals = signals.ToImmutable()
                });
        }
    }

    private void AnalyzeTcpOptions(string options, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        // TCP options fingerprinting (similar to p0f)
        // Format: MSS,SACK,Timestamp,WindowScale,etc.

        var hasTimestamp = options.Contains("TS", StringComparison.OrdinalIgnoreCase);
        var hasSack = options.Contains("SACK", StringComparison.OrdinalIgnoreCase);
        var hasWindowScale = options.Contains("WS", StringComparison.OrdinalIgnoreCase);

        signals.Add("tcp.has_timestamp", hasTimestamp);
        signals.Add("tcp.has_sack", hasSack);
        signals.Add("tcp.has_window_scale", hasWindowScale);

        // Modern browsers typically have all these options
        var modernOptions = hasTimestamp && hasSack && hasWindowScale;
        signals.Add("tcp.modern_options", modernOptions);

        if (!modernOptions)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "TCP/IP",
                ConfidenceDelta = 0.2,
                Weight = 0.9,
                Reason = "Missing modern network features that real browsers include",
                Signals = signals.ToImmutable()
            });

        // Very minimal options = likely bot/old client
        if (options.Split(',').Length <= 2)
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "TCP/IP",
                ConfidenceDelta = 0.25,
                Weight = 1.0,
                Reason = "Very few network options set (typical for automation tools, not real browsers)",
                Signals = signals.ToImmutable()
            });
    }

    private void AnalyzeMss(int mss, List<DetectionContribution> contributions,
        ImmutableDictionary<string, object>.Builder signals)
    {
        // Standard MSS values: 1460 (Ethernet), 1440 (PPPoE), 536 (default)

        if (mss == 536)
            // Very old default or custom stack
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "TCP/IP",
                ConfidenceDelta = 0.3,
                Weight = 1.1,
                Reason = "Minimal network packet size (indicates old or custom networking, not a real browser)",
                Signals = signals.ToImmutable()
            });
        else if (mss < 536 || mss > 1460)
            // Unusual MSS
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "TCP/IP",
                ConfidenceDelta = 0.15,
                Weight = 0.8,
                Reason = "Non-standard network packet size (does not match standard browsers)",
                Signals = signals.ToImmutable()
            });
    }

    private static bool IsPowerOfTwo(int n)
    {
        return (n & (n - 1)) == 0 && n != 0;
    }
}