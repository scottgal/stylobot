using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) that fingerprints HTTP/3 (QUIC)
///     connections: transport-parameter shape, version, 0-RTT resumption,
///     connection migration, spin-bit cooperation, and Alt-Svc upgrade
///     path. Priority 14 -- Wave 0.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>Http3FingerprintContributor</c>.
///     </para>
///     <para>
///         Reads X-QUIC-* edge-injected headers gated by
///         <see cref="ITransportHeaderTrust"/> -- an untrusted peer sending
///         these headers is treated as a spoof attempt, not a fingerprint.
///     </para>
///     <para>
///         KnownTransportFingerprints + BotClientPatterns carried over
///         verbatim from the contributor -- catalog cleanup to YAML per
///         feedback_no_word_lists is a separate task; parity comes first.
///     </para>
/// </remarks>
public sealed class Http3FingerprintAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists.
    private static readonly Dictionary<string, string> KnownTransportFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        { "initial_max_data=15728640", "Chrome_QUIC" },
        { "initial_max_data=15728640,initial_max_stream_data_bidi_local=6291456", "Chrome_QUIC_Full" },
        { "initial_max_data=10485760", "Firefox_QUIC" },
        { "initial_max_data=10485760,initial_max_stream_data_bidi_local=2097152", "Firefox_QUIC_Full" },
        { "initial_max_data=8388608", "Safari_QUIC" },
        { "initial_max_data=8388608,initial_max_stream_data_bidi_local=1048576", "Safari_QUIC_Full" },
        { "initial_max_data=15728640,initial_max_streams_bidi=256", "Edge_QUIC" },
        { "initial_max_data=1048576", "Go_QuicGo" },
        { "initial_max_data=1048576,initial_max_stream_data_bidi_local=524288", "Go_QuicGo_Full" },
        { "initial_max_data=2097152", "Python_Aioquic" },
        { "initial_max_data=2097152,initial_max_stream_data_bidi_local=1048576", "Python_Aioquic_Full" },
        { "initial_max_data=8388608,initial_max_streams_bidi=100", "Rust_Quinn" },
        { "initial_max_data=10000000", "Curl_Quiche" },
        { "initial_max_data=65536", "Custom_Minimal_QUIC" },
        { "initial_max_data=131072", "Custom_Small_QUIC" }
    };

    private static readonly HashSet<string> BotClientPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Go_QuicGo", "Go_QuicGo_Full",
        "Python_Aioquic", "Python_Aioquic_Full",
        "Rust_Quinn",
        "Curl_Quiche",
        "Custom_Minimal_QUIC", "Custom_Small_QUIC"
    };

    private readonly ILogger<Http3FingerprintAtom> _logger;
    private readonly ITransportHeaderTrust? _transportTrust;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public Http3FingerprintAtom(
        ILogger<Http3FingerprintAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        ITransportHeaderTrust? transportTrust = null)
        : base(name: "Http3Fingerprint", category: "HTTP/3")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _transportTrust = transportTrust;
    }

    public override int Priority => 14;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double QuicBotConfidence => _configProvider.GetParameter(Name, "quic_bot_confidence", 0.6);
    private double QuicBrowserConfidence => _configProvider.GetParameter(Name, "quic_browser_confidence", -0.2);
    private double ZeroRttHumanBonus => _configProvider.GetParameter(Name, "zero_rtt_human_bonus", -0.15);
    private double ConnectionMigrationHumanBonus => _configProvider.GetParameter(Name, "connection_migration_human_bonus", -0.1);
    private double DraftVersionPenalty => _configProvider.GetParameter(Name, "draft_version_penalty", 0.3);
    private double AltSvcUpgradeBonus => _configProvider.GetParameter(Name, "alt_svc_upgrade_bonus", -0.2);
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

        sink.Raise("h3.ran", sessionId);

        var contributions = new List<DetectionContribution>();
        var req = context.Request;

        // ITransportHeaderTrust needs BlackboardState in the legacy path; under
        // the pack we pass a minimal shim -- but the trust service treats
        // missing state as "not trusted" which is safe. Fall back to the same
        // "trust by default" semantics when the service isn't wired.
        var trustHeaders = _transportTrust is null;

        try
        {
            if (_transportTrust is not null)
            {
                // Reconstruct a light-weight BlackboardState-shaped view. The
                // trust service reads state.HttpContext primarily; other
                // fields are unused. Handled the same way as legacy for parity.
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
                    var gatedHeaderPresent =
                        req.Headers.ContainsKey("X-QUIC-Transport-Params")
                        || req.Headers.ContainsKey("X-QUIC-Version")
                        || req.Headers.ContainsKey("X-QUIC-0RTT")
                        || req.Headers.ContainsKey("X-QUIC-Connection-Migrated")
                        || req.Headers.ContainsKey("X-QUIC-Spin-Bit")
                        || req.Headers.ContainsKey("X-QUIC-Alt-Svc-Used");

                    if (gatedHeaderPresent)
                    {
                        sink.Raise($"{SignalKeys.TransportSpoofedEdgeHeaders}:true", sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = SpoofedEdgeHeadersConfidence,
                            Weight = SpoofedEdgeHeadersWeight,
                            Reason = "Edge QUIC fingerprint headers from an untrusted direct peer (possible spoof)",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                }
            }

            var protocol = req.Protocol;
            sink.Raise($"{SignalKeys.H3Protocol}:{protocol}", sessionId);

            var isHttp3 = protocol.Equals("HTTP/3", StringComparison.OrdinalIgnoreCase)
                          || protocol.Equals("HTTP/3.0", StringComparison.OrdinalIgnoreCase);
            sink.Raise($"{SignalKeys.H3IsHttp3}:{(isHttp3 ? "true" : "false")}", sessionId);

            if (!isHttp3)
            {
                contributions.Add(DetectionContribution.Info(Name, Category, $"Connection uses {protocol} (not HTTP/3)"));
                return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
            }

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -0.1,
                Weight = WeightHumanSignal * 0.8,
                Reason = "Using HTTP/3 (QUIC) - most bot frameworks don't support this protocol"
            });

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-Transport-Params", out var transportParams))
            {
                var paramStr = transportParams.ToString();
                sink.Raise($"h3.transport_params:{paramStr}", sessionId);

                var matchedClient = MatchTransportFingerprint(paramStr);
                if (matchedClient is not null)
                {
                    sink.Raise($"{SignalKeys.H3ClientType}:{matchedClient}", sessionId);
                    if (BotClientPatterns.Contains(matchedClient))
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = QuicBotConfidence,
                            Weight = 1.6,
                            Reason = $"QUIC transport parameters match known automation client: {matchedClient}",
                            BotType = BotType.Scraper.ToString()
                        });
                    }
                    else
                    {
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = QuicBrowserConfidence,
                            Weight = WeightHumanSignal * 1.4,
                            Reason = $"QUIC transport parameters match browser: {matchedClient}"
                        });
                    }
                }
                else
                {
                    sink.Raise("h3.transport_fingerprint_unknown:true", sessionId);
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-Version", out var quicVersion))
            {
                var version = quicVersion.ToString();
                sink.Raise($"h3.quic_version:{version}", sessionId);

                if (version.Contains("draft", StringComparison.OrdinalIgnoreCase))
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = DraftVersionPenalty,
                        Weight = 1.2,
                        Reason = $"Using QUIC draft version ({version}) - indicates old or custom tooling",
                        BotType = BotType.Unknown.ToString()
                    });
                }
                else if (version.Contains("v2", StringComparison.OrdinalIgnoreCase)
                         || version.Contains("0x6b3343cf", StringComparison.OrdinalIgnoreCase))
                {
                    sink.Raise("h3.quic_v2:true", sessionId);
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = -0.1,
                        Weight = WeightHumanSignal,
                        Reason = "Using QUIC v2 (RFC 9369) - very modern browser"
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-0RTT", out var zeroRtt))
            {
                var usesZeroRtt = ParseFlagHeader(zeroRtt.ToString());
                sink.Raise($"{SignalKeys.H3ZeroRtt}:{(usesZeroRtt ? "true" : "false")}", sessionId);
                if (usesZeroRtt)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = ZeroRttHumanBonus,
                        Weight = WeightHumanSignal * 1.3,
                        Reason = "QUIC 0-RTT resumption used - returning visitor with session cache"
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-Connection-Migrated", out var migrated))
            {
                var hasMigrated = ParseFlagHeader(migrated.ToString());
                sink.Raise($"{SignalKeys.H3ConnectionMigrated}:{(hasMigrated ? "true" : "false")}", sessionId);
                if (hasMigrated)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = ConnectionMigrationHumanBonus,
                        Weight = WeightHumanSignal * 1.2,
                        Reason = "QUIC connection migration detected - mobile user switching networks"
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-Spin-Bit", out var spinBit))
            {
                var spinDisabled = spinBit.ToString().Equals("0", StringComparison.OrdinalIgnoreCase)
                                   || spinBit.ToString().Equals("false", StringComparison.OrdinalIgnoreCase);
                sink.Raise($"h3.spin_bit_disabled:{(spinDisabled ? "true" : "false")}", sessionId);
                if (spinDisabled)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = 0.15,
                        Weight = 0.6,
                        Reason = "QUIC spin bit disabled - some bots don't cooperate with RTT measurement",
                        BotType = BotType.Unknown.ToString()
                    });
                }
            }

            if (trustHeaders && req.Headers.TryGetValue("X-QUIC-Alt-Svc-Used", out var altSvc))
            {
                var usedAltSvc = ParseFlagHeader(altSvc.ToString());
                sink.Raise($"h3.alt_svc_upgrade:{(usedAltSvc ? "true" : "false")}", sessionId);
                if (usedAltSvc)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = AltSvcUpgradeBonus,
                        Weight = WeightHumanSignal * 1.5,
                        Reason = "Arrived via Alt-Svc HTTP/2 to HTTP/3 upgrade - bots rarely negotiate this"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing HTTP/3 fingerprint");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "HTTP/3 analysis complete (no anomalies detected)"));

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private static bool ParseFlagHeader(string value)
        => value.Equals("1", StringComparison.OrdinalIgnoreCase)
           || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string? MatchTransportFingerprint(string transportParams)
    {
        foreach (var (fingerprint, client) in KnownTransportFingerprints)
        {
            if (transportParams.Contains(fingerprint, StringComparison.OrdinalIgnoreCase))
                return client;
        }
        return null;
    }
}
