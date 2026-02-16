using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     HTTP/3 (QUIC) fingerprinting contributor.
///     Analyzes QUIC transport parameters, version negotiation, 0-RTT resumption,
///     and connection migration to detect automation on HTTP/3 connections.
///     Best-in-breed approach:
///     - QUIC transport parameter fingerprinting (initial_max_data values)
///     - QUIC version analysis (v1, v2, draft versions)
///     - 0-RTT resumption detection (returning human visitors)
///     - Connection migration (mobile users switching networks)
///     - Spin bit cooperation
///     - Alt-Svc upgrade detection (HTTP/2 -> HTTP/3 negotiation)
///     Raises signals:
///     - h3.protocol
///     - h3.client_type
///     - h3.zero_rtt
///     - h3.connection_migrated
///
///     Configuration loaded from: http3.detector.yaml
///     Override via: appsettings.json -> BotDetection:Detectors:Http3FingerprintContributor:*
/// </summary>
public class Http3FingerprintContributor : ConfiguredContributorBase
{
    // Known QUIC transport parameter fingerprints (initial_max_data values)
    // Browsers negotiate specific initial flow control values
    private static readonly Dictionary<string, string> KnownTransportFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chrome/Chromium (initial_max_data=15728640 = 15MB)
        { "initial_max_data=15728640", "Chrome_QUIC" },
        { "initial_max_data=15728640,initial_max_stream_data_bidi_local=6291456", "Chrome_QUIC_Full" },

        // Firefox (initial_max_data=10485760 = 10MB)
        { "initial_max_data=10485760", "Firefox_QUIC" },
        { "initial_max_data=10485760,initial_max_stream_data_bidi_local=2097152", "Firefox_QUIC_Full" },

        // Safari/WebKit (initial_max_data=8388608 = 8MB)
        { "initial_max_data=8388608", "Safari_QUIC" },
        { "initial_max_data=8388608,initial_max_stream_data_bidi_local=1048576", "Safari_QUIC_Full" },

        // Edge (Chromium-based, same as Chrome)
        { "initial_max_data=15728640,initial_max_streams_bidi=256", "Edge_QUIC" },

        // ====================================
        // Bot/Automation QUIC Fingerprints
        // ====================================

        // Go quic-go library
        { "initial_max_data=1048576", "Go_QuicGo" },
        { "initial_max_data=1048576,initial_max_stream_data_bidi_local=524288", "Go_QuicGo_Full" },

        // Python aioquic library
        { "initial_max_data=2097152", "Python_Aioquic" },
        { "initial_max_data=2097152,initial_max_stream_data_bidi_local=1048576", "Python_Aioquic_Full" },

        // Rust quinn library
        { "initial_max_data=8388608,initial_max_streams_bidi=100", "Rust_Quinn" },

        // curl with quiche
        { "initial_max_data=10000000", "Curl_Quiche" },

        // Minimal/custom stacks
        { "initial_max_data=65536", "Custom_Minimal_QUIC" },
        { "initial_max_data=131072", "Custom_Small_QUIC" }
    };

    // Known bot QUIC client identifiers
    private static readonly HashSet<string> BotClientPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Go_QuicGo", "Go_QuicGo_Full",
        "Python_Aioquic", "Python_Aioquic_Full",
        "Rust_Quinn",
        "Curl_Quiche",
        "Custom_Minimal_QUIC", "Custom_Small_QUIC"
    };

    private readonly ILogger<Http3FingerprintContributor> _logger;

    public Http3FingerprintContributor(
        ILogger<Http3FingerprintContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "Http3Fingerprint";
    public override int Priority => Manifest?.Priority ?? 14;

    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private double QuicBotConfidence => GetParam("quic_bot_confidence", 0.6);
    private double QuicBrowserConfidence => GetParam("quic_browser_confidence", -0.2);
    private double ZeroRttHumanBonus => GetParam("zero_rtt_human_bonus", -0.15);
    private double ConnectionMigrationHumanBonus => GetParam("connection_migration_human_bonus", -0.1);
    private double DraftVersionPenalty => GetParam("draft_version_penalty", 0.3);
    private double AltSvcUpgradeBonus => GetParam("alt_svc_upgrade_bonus", -0.2);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var protocol = state.HttpContext.Request.Protocol;
            state.WriteSignal(SignalKeys.H3Protocol, protocol);

            // Only proceed if HTTP/3
            var isHttp3 = protocol.Equals("HTTP/3", StringComparison.OrdinalIgnoreCase) ||
                          protocol.Equals("HTTP/3.0", StringComparison.OrdinalIgnoreCase);

            state.WriteSignal("h3.is_http3", isHttp3);

            if (!isHttp3)
            {
                // Not HTTP/3 — nothing for this contributor to do
                contributions.Add(DetectionContribution.Info(Name, "HTTP/3",
                    $"Connection uses {protocol} (not HTTP/3)"));
                return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
            }

            // HTTP/3 itself is a mildly positive (human) signal — most bot frameworks don't support QUIC yet
            contributions.Add(HumanContribution("HTTP/3",
                "Using HTTP/3 (QUIC) — most bot frameworks don't support this protocol",
                weightMultiplier: 0.8));

            // 1. QUIC Transport Parameter fingerprinting
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-Transport-Params", out var transportParams))
            {
                var paramStr = transportParams.ToString();
                state.WriteSignal("h3.transport_params", paramStr);

                var matchedClient = MatchTransportFingerprint(paramStr);
                if (matchedClient != null)
                {
                    state.WriteSignal(SignalKeys.H3ClientType, matchedClient);

                    if (BotClientPatterns.Contains(matchedClient))
                    {
                        contributions.Add(BotContribution(
                            "HTTP/3",
                            $"QUIC transport parameters match known automation client: {matchedClient}",
                            confidenceOverride: QuicBotConfidence,
                            weightMultiplier: 1.6,
                            botType: BotType.Scraper.ToString()));
                    }
                    else
                    {
                        contributions.Add(HumanContribution(
                            "HTTP/3",
                            $"QUIC transport parameters match browser: {matchedClient}")
                            with
                            {
                                ConfidenceDelta = QuicBrowserConfidence,
                                Weight = WeightHumanSignal * 1.4
                            });
                    }
                }
                else
                {
                    state.WriteSignal("h3.transport_fingerprint_unknown", true);
                }
            }

            // 2. QUIC version analysis
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-Version", out var quicVersion))
            {
                var version = quicVersion.ToString();
                state.WriteSignal("h3.quic_version", version);

                if (version.Contains("draft", StringComparison.OrdinalIgnoreCase))
                {
                    // Draft versions = old tooling, suspicious
                    contributions.Add(BotContribution(
                        "HTTP/3",
                        $"Using QUIC draft version ({version}) — indicates old or custom tooling",
                        confidenceOverride: DraftVersionPenalty,
                        weightMultiplier: 1.2));
                }
                else if (version.Contains("v2", StringComparison.OrdinalIgnoreCase) ||
                         version.Contains("0x6b3343cf", StringComparison.OrdinalIgnoreCase))
                {
                    // QUIC v2 (RFC 9369) = very modern browser
                    state.WriteSignal("h3.quic_v2", true);
                    contributions.Add(HumanContribution("HTTP/3",
                        "Using QUIC v2 (RFC 9369) — very modern browser"));
                }
                // v1 (RFC 9000) is standard, no additional signal needed
            }

            // 3. 0-RTT resumption detection
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-0RTT", out var zeroRtt))
            {
                var usesZeroRtt = zeroRtt.ToString().Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                  zeroRtt.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                state.WriteSignal(SignalKeys.H3ZeroRtt, usesZeroRtt);

                if (usesZeroRtt)
                {
                    // 0-RTT = returning visitor with session cache, strong human signal
                    contributions.Add(HumanContribution("HTTP/3",
                        "QUIC 0-RTT resumption used — returning visitor with session cache")
                        with
                        {
                            ConfidenceDelta = ZeroRttHumanBonus,
                            Weight = WeightHumanSignal * 1.3
                        });
                }
            }

            // 4. Connection migration detection
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-Connection-Migrated", out var migrated))
            {
                var hasMigrated = migrated.ToString().Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                  migrated.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                state.WriteSignal(SignalKeys.H3ConnectionMigrated, hasMigrated);

                if (hasMigrated)
                {
                    // Connection migration = mobile user switching networks (WiFi -> cellular)
                    contributions.Add(HumanContribution("HTTP/3",
                        "QUIC connection migration detected — mobile user switching networks")
                        with
                        {
                            ConfidenceDelta = ConnectionMigrationHumanBonus,
                            Weight = WeightHumanSignal * 1.2
                        });
                }
            }

            // 5. Spin bit analysis
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-Spin-Bit", out var spinBit))
            {
                var spinDisabled = spinBit.ToString().Equals("0", StringComparison.OrdinalIgnoreCase) ||
                                   spinBit.ToString().Equals("false", StringComparison.OrdinalIgnoreCase);
                state.WriteSignal("h3.spin_bit_disabled", spinDisabled);

                if (spinDisabled)
                {
                    // Disabled spin bit = some bots don't cooperate with RTT measurement
                    contributions.Add(BotContribution(
                        "HTTP/3",
                        "QUIC spin bit disabled — some bots don't cooperate with RTT measurement",
                        confidenceOverride: 0.15,
                        weightMultiplier: 0.6));
                }
            }

            // 6. Alt-Svc upgrade detection (HTTP/2 -> HTTP/3)
            if (state.HttpContext.Request.Headers.TryGetValue("X-QUIC-Alt-Svc-Used", out var altSvc))
            {
                var usedAltSvc = altSvc.ToString().Equals("1", StringComparison.OrdinalIgnoreCase) ||
                                 altSvc.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                state.WriteSignal("h3.alt_svc_upgrade", usedAltSvc);

                if (usedAltSvc)
                {
                    // Arrived via Alt-Svc upgrade from HTTP/2 = strong human signal
                    // Bots rarely negotiate this upgrade path
                    contributions.Add(HumanContribution("HTTP/3",
                        "Arrived via Alt-Svc HTTP/2 to HTTP/3 upgrade — bots rarely negotiate this")
                        with
                        {
                            ConfidenceDelta = AltSvcUpgradeBonus,
                            Weight = WeightHumanSignal * 1.5
                        });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing HTTP/3 fingerprint");
            state.WriteSignal("h3.analysis_error", ex.Message);
        }

        // If no contributions, add neutral
        if (contributions.Count == 0)
        {
            contributions.Add(DetectionContribution.Info(
                Name,
                "HTTP/3",
                "HTTP/3 analysis complete (no anomalies detected)"));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static string? MatchTransportFingerprint(string transportParams)
    {
        foreach (var (fingerprint, client) in KnownTransportFingerprints)
            if (transportParams.Contains(fingerprint, StringComparison.OrdinalIgnoreCase))
                return client;

        return null;
    }
}
