using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that runs late-stage AI / ONNX / LLM
///     analysis only when the running risk score has already crossed a
///     medium-risk threshold and enough detectors have contributed.
///     Priority 100 -- runs after every other atom; reads the running risk
///     score directly from the sink.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>AiContributor</c>. The blackboard IS the SignalSink -- the
///         orchestrator now raises <c>risk.current_score</c> and
///         <c>contribution.*</c> signals, so this atom reads its inputs
///         directly from the sink without a separate ledger-access contract.
///     </para>
///     <para>
///         Preserves the contributor's placeholder body (a small delay +
///         confirmation contribution based on the running risk band) --
///         production AI wiring is a separate follow-on that plugs an ONNX
///         session or LLM here.
///     </para>
/// </remarks>
public sealed class AiAtom : DetectorAtomBase
{
    private readonly ILogger<AiAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;

    public AiAtom(
        ILogger<AiAtom> logger,
        IDetectorConfigProvider configProvider)
        : base(name: "AI", category: "AI")
    {
        _logger = logger;
        _configProvider = configProvider;
    }

    public override int Priority => 100;
    // Must run AFTER the wave that produced contributions — the orchestrator raises
    // contribution.<detector>.<index> between waves; requiring them lands this atom in
    // Wave 1 so detectorCount is non-zero (review A1, 2026-08-28).
    public override IReadOnlyList<string> RequiredSignals => new[] { "contribution.*" };

    public override TimeSpan Timeout
    {
        get
        {
            var configured = _configProvider.GetDefaults(Name).Timing.TimeoutMs;
            return TimeSpan.FromMilliseconds(configured > 0 ? configured : 5000);
        }
    }

    private double HighRiskThreshold => _configProvider.GetParameter(Name, "high_risk_threshold", 0.8);
    private double MediumRiskThreshold => _configProvider.GetParameter(Name, "medium_risk_threshold", 0.5);
    private double HighRiskAdjustment => _configProvider.GetParameter(Name, "high_risk_adjustment", 0.2);
    private int MinDetectorCount => _configProvider.GetParameter(Name, "min_detector_count", 2);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Read the running risk score + detector participation directly off
        // the sink. The ephemeral orchestrator publishes both between waves;
        // no BlackboardState / ledger reach-through needed.
        var currentRisk = SinkEvidenceReader.ReadCurrentRiskScore(sink);
        var detectorCount = SinkEvidenceReader.ReadContributingDetectorNames(sink).Count;

        // Guard: only run when risk is medium-or-higher AND enough detectors
        // have produced contributions. Matches the legacy AllOf trigger.
        if (currentRisk < MediumRiskThreshold || detectorCount < MinDetectorCount)
            return None();
        _logger.LogDebug("AI detector running for request {RequestId} (risk={Risk:F2}, detectors={Count})",
            sessionId, currentRisk, detectorCount);

        // Production AI wiring (ONNX session / LLM) is a follow-on — the AiContributor
        // in the LLM project handles that. This atom runs late-stage risk confirmation
        // without artificial latency.
        if (currentRisk > HighRiskThreshold)
        {
            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = HighRiskAdjustment,
                Weight = 0.5,
                Reason = "AI analysis confirms high-risk signals",
                BotType = BotType.Unknown.ToString()
            });
        }

        return Single(DetectionContribution.Info(Name, Category, "AI analysis: borderline case, monitoring"));
    }
}
