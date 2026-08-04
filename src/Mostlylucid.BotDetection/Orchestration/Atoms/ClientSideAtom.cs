using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that analyses the client-side
///     browser fingerprint captured by <see cref="ClientSideDetector"/>.
///     Priority 18, Wave 0 -- runs alongside header analysis but the JS
///     probe's presence itself is a strong human attestation.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ClientSideContributor</c>. Three code paths preserved from the
///         legacy contributor:
///         <list type="bullet">
///             <item>
///                 <b>Adblocker-only beacon</b> -- exclusive human-affinity
///                 signal, returns early so the no-fingerprint penalty does
///                 not double-fire.
///             </item>
///             <item>
///                 <b>No-fingerprint on document request</b> -- calibrated
///                 penalty against <see cref="FingerprintPopulationTracker"/>
///                 so bots don't get flagged in populations that just don't
///                 send the probe.
///             </item>
///             <item>
///                 <b>Fingerprint present</b> -- emit every JS signal the
///                 detector produced as a Model-2 hint. Signals are numeric
///                 metrics, short labels, and hashes -- no PII.
///             </item>
///         </list>
///     </para>
///     <para>
///         Raw fingerprint payload stays on
///         <see cref="HttpContext.Items"/>["__mlbotd_fingerprint"] (the
///         existing rich-state carrier the fingerprint pipeline already
///         uses); the atom only lifts scalar values off it onto the sink.
///     </para>
/// </remarks>
public sealed class ClientSideAtom : DetectorAtomBase
{
    private const string FingerprintItemsKey = "__mlbotd_fingerprint";

    private readonly ClientSideDetector _detector;
    private readonly ILogger<ClientSideAtom> _logger;
    private readonly FingerprintPopulationTracker _population;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ClientSideAtom(
        ILogger<ClientSideAtom> logger,        FingerprintPopulationTracker population,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "ClientSide", category: "ClientSide")
    {
        _logger = logger;
        _detector = new ClientSideDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientSideDetector>.Instance, Microsoft.Extensions.Options.Options.Create(new Mostlylucid.BotDetection.Models.BotDetectionOptions()), null!, null, null);
        _population = population;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 18;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(context, ct).ConfigureAwait(false);
            if (result.Reasons.Count == 0) return None();

            var transportClass = sink.ReadHint(SignalKeys.TransportProtocolClass) ?? TransportClasses.Unknown;
            var uaFamily = sink.ReadHint(SignalKeys.UserAgentFamily) ?? TransportClasses.Unknown;

            var isNoFingerprint = result.Reasons.Count == 1
                                  && result.Reasons[0].Detail == ClientSideReasons.NoFingerprint;
            var isAdblockerOnly = result.Reasons.Count == 1
                                  && result.Reasons[0].Detail == ClientSideReasons.AdblockerDetected;

            if (isAdblockerOnly)
            {
                sink.Raise($"{SignalKeys.ClientSideAdblockerDetected}:true", sessionId);
                if (context.Items[FingerprintItemsKey] is BrowserFingerprintResult fpAd
                    && !string.IsNullOrEmpty(fpAd.AdblockerProvider))
                {
                    sink.Raise($"{SignalKeys.ClientSideAdblockerProvider}:{fpAd.AdblockerProvider}", sessionId);
                }

                if (transportClass == TransportClasses.Document)
                    _population.Record(uaFamily, transportClass, hasFingerprint: true);

                var bias = _configProvider.GetParameter(Name, "adblocker_human_bias", -0.05);
                if (Math.Abs(bias) >= 0.001)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = bias,
                        Weight = _configProvider.GetParameter(Name, "fingerprint_weight", 1.8),
                        Reason = "Adblocker probe blocked: human-affinity bias (adblocker users skew strongly human)"
                    });
                }
                return contributions;
            }

            if (isNoFingerprint)
            {
                if (transportClass == TransportClasses.Document)
                {
                    var (rate, samples) = _population.Record(uaFamily, transportClass, hasFingerprint: false);

                    if (_configProvider.GetParameter(Name, "penalize_no_fingerprint", true))
                    {
                        var minSamples = _configProvider.GetParameter(Name, "population_min_samples", 20);
                        var rateThreshold = _configProvider.GetParameter(Name, "population_rate_threshold", 0.7);
                        var penaltyBase = _configProvider.GetParameter(Name, "penalize_confidence", 0.15);

                        double bias;
                        string reason;

                        if (samples >= minSamples)
                        {
                            if (rate < rateThreshold) return contributions;
                            bias = penaltyBase * rate;
                            reason = $"Document request without fingerprint; {rate:P0} of similar requests carry one ({samples} samples)";
                        }
                        else
                        {
                            bias = penaltyBase * 0.5;
                            reason = $"Document request without fingerprint; population data insufficient ({samples} samples)";
                        }

                        sink.Raise(
                            $"{SignalKeys.ClientSideNoFingerprintBias}:{bias.ToString("F4", CultureInfo.InvariantCulture)}",
                            sessionId);
                        contributions.Add(new DetectionContribution
                        {
                            DetectorName = Name,
                            Category = Category,
                            ConfidenceDelta = bias,
                            Weight = _configProvider.GetParameter(Name, "fingerprint_weight", 1.8),
                            Reason = reason
                        });
                    }
                }
                return contributions;
            }

            // Fingerprint found: record population + emit per-reason contribution.
            if (transportClass == TransportClasses.Document)
                _population.Record(uaFamily, transportClass, hasFingerprint: true);

            foreach (var reason in result.Reasons)
            {
                if (Math.Abs(reason.ConfidenceImpact) < 0.001) continue;

                sink.Raise(
                    $"{SignalKeys.FingerprintHeadlessScore}:{(reason.Detail.Contains("Headless") ? "1" : "0")}",
                    sessionId);
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = reason.Category,
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = _configProvider.GetParameter(Name, "fingerprint_weight", 1.8),
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName
                });
            }

            // Scalar lifts off the rich BrowserFingerprintResult object.
            if (context.Items[FingerprintItemsKey] is BrowserFingerprintResult fp)
            {
                if (fp.LayoutTimeMs.HasValue)
                    sink.Raise($"{SignalKeys.JsLayoutTimeMs}:{fp.LayoutTimeMs.Value.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                if (fp.SetTimeoutDrift.HasValue)
                    sink.Raise($"{SignalKeys.JsSetTimeoutDrift}:{fp.SetTimeoutDrift.Value.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                if (fp.PerformanceResolution.HasValue)
                    sink.Raise($"{SignalKeys.JsPerformanceResolution}:{fp.PerformanceResolution.Value.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
                if (fp.TimingAnomaly)
                    sink.Raise($"{SignalKeys.JsTimingAnomaly}:true", sessionId);
                if (!string.IsNullOrEmpty(fp.DetectedAutomation))
                    sink.Raise($"{SignalKeys.HeadlessFramework}:{fp.DetectedAutomation}", sessionId);
                if (!string.IsNullOrEmpty(fp.ConnectionType))
                    sink.Raise($"{SignalKeys.ClientSideConnectionType}:{fp.ConnectionType}", sessionId);
                if (fp.IceNoSrflx.HasValue)
                    sink.Raise($"{SignalKeys.ClientSideIceNoSrflx}:{(fp.IceNoSrflx.Value ? "true" : "false")}", sessionId);
                if (fp.TtsVoiceCount.HasValue)
                    sink.Raise($"{SignalKeys.ClientSideTtsVoiceCount}:{fp.TtsVoiceCount.Value}", sessionId);
                if (!string.IsNullOrEmpty(fp.BotdKind))
                    sink.Raise($"{SignalKeys.ClientSideBotdKind}:{fp.BotdKind}", sessionId);
                if (!string.IsNullOrEmpty(fp.ShapeHash))
                    sink.Raise($"{SignalKeys.ClientSideShapeHash}:{fp.ShapeHash}", sessionId);
                if (fp.MouseMoveCount.HasValue)
                    sink.Raise($"{SignalKeys.ClientMouseEvents}:{fp.MouseMoveCount.Value}", sessionId);
                if (fp.MouseAllIntegerCoords.HasValue)
                    sink.Raise($"{SignalKeys.ClientSideMouseAllIntegerCoords}:{(fp.MouseAllIntegerCoords.Value ? "true" : "false")}", sessionId);
                if (fp.MouseTimingCv.HasValue)
                    sink.Raise($"{SignalKeys.ClientSideMouseTimingCv}:{fp.MouseTimingCv.Value.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);

                // Browser-characteristic consistency (script v2.1.0+): inert raw
                // observations. The engine tells are the un-spoofable anchor; the
                // feature-presence count is the spoofable summary. The browser_char
                // centroid scores the full vector off fp.Engine/fp.Features; these
                // signals give the consistency branch its presence-trigger plus
                // dashboard/BDF observability. Only raised when a beacon arrived,
                // so this stays a triggered path, never foundation.
                if (fp.Engine is { } eng)
                {
                    if (!string.IsNullOrEmpty(eng.StackStyle))
                        sink.Raise($"{SignalKeys.ClientSideEngineFamily}:{eng.StackStyle}", sessionId);
                    var v8 = eng.V8BreakIterator == 1 || eng.ErrorCaptureStackTrace == 1;
                    sink.Raise($"{SignalKeys.ClientSideEngineV8}:{(v8 ? "true" : "false")}", sessionId);
                }
                if (fp.Features is { } feat)
                {
                    var present = 0;
                    if (feat.Popover == 1) present++;
                    if (feat.CssHas == 1) present++;
                    if (feat.ArrayFindLast == 1) present++;
                    if (feat.StructuredClone == 1) present++;
                    if (feat.WebGpu == 1) present++;
                    sink.Raise($"{SignalKeys.ClientSideFeatureCount}:{present}", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ClientSide detection failed");
        }

        return contributions;
    }
}
