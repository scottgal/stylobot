using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that runs as the final meta-layer after
///     every other atom has emitted its contribution. Reads prior
///     contributions AND the running risk score directly from the shared
///     <see cref="SignalSink"/> -- no ledger-access contract needed --
///     because the ephemeral orchestrator now publishes both as sink signals.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>HeuristicLateContributor</c>. Priority 100 -- the highest, so
///         every wave that could contribute has completed.
///     </para>
///     <para>
///         Builds a lightweight <see cref="AggregatedEvidence"/> via
///         <see cref="SinkEvidenceReader.BuildEvidence"/> and drops it on
///         <see cref="HttpContext.Items"/> under
///         <see cref="BotDetectionMiddleware.AggregatedEvidenceKey"/> so
///         the wrapped <see cref="HeuristicDetector"/> sees "full mode".
///     </para>
///     <para>
///         Triggers: at least one of AiPrediction / AiConfidence /
///         HeuristicPrediction must be present on the sink -- indicates
///         earlier detection stages have run.
///     </para>
/// </remarks>
public sealed class HeuristicLateAtom : DetectorAtomBase
{
    private readonly HeuristicDetector _detector;
    private readonly ILogger<HeuristicLateAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HeuristicLateAtom(
        ILogger<HeuristicLateAtom> logger,        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "HeuristicLate", category: "HeuristicLate")
    {
        _logger = logger;
        _detector = new HeuristicDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<HeuristicDetector>.Instance, Microsoft.Extensions.Options.Options.Create(new Mostlylucid.BotDetection.Models.BotDetectionOptions()));
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 100;
    // Must run AFTER HeuristicAtom (Wave 0) raises heuristic.prediction — otherwise it starts
    // concurrently and races the full-mode pass it exists to perform (review A1, 2026-08-28).
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.HeuristicPrediction };

    private double LateWeight => _configProvider.GetParameter(Name, "late_weight", 2.5);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // OR-arm trigger evaluation: run only after some earlier stage has
        // produced its prediction. Otherwise there's nothing for the "full
        // mode" pass to combine.
        var hasPriorStage = sink.ReadHint(SignalKeys.AiPrediction) is not null
                            || sink.ReadHint(SignalKeys.AiConfidence) is not null
                            || sink.ReadHint(SignalKeys.HeuristicPrediction) is not null;
        if (!hasPriorStage) return None();

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        var contributions = new List<DetectionContribution>();

        try
        {
            var tempEvidence = SinkEvidenceReader.BuildEvidence(sink, context, "temp-heuristic-late");
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = tempEvidence;

            var result = await _detector.DetectAsync(context, ct).ConfigureAwait(false);

            if (result.Reasons.Count == 0) return contributions;

            var reason = result.Reasons.First();
            var isBot = reason.ConfidenceImpact > 0;

            sink.Raise($"{SignalKeys.HeuristicLatePrediction}:{(isBot ? "bot" : "human")}", sessionId);
            sink.Raise($"{SignalKeys.HeuristicLateConfidence}:{result.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);

            var resolvedBotType = result.BotType is { } bt and not BotType.Unknown
                ? bt.ToString()
                : null;

            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = reason.ConfidenceImpact,
                Weight = LateWeight,
                Reason = reason.Detail.Replace("(early)", "(late)").Replace("(full)", "(late)"),
                BotType = resolvedBotType,
                BotName = result.BotName
            });

            _logger.LogDebug(
                "Late heuristic completed: {Prediction} with confidence {Confidence:F2}",
                isBot ? "bot" : "human", result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Late heuristic detection failed");
        }

        return contributions;
    }
}
