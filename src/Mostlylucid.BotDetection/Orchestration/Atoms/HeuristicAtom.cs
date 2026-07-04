using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Heuristic ProposerAtom (per Taxonomy.md) — uses learned weights for bot
///     classification. Runs in Wave 1+ after initial sensor atoms have populated
///     the sink with features.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>HeuristicContributor</c>. Delegates to the shared
///         <see cref="HeuristicDetector"/> service (feature-vector inference
///         over the request), raises heuristic.* signals on the sink, and
///         returns a single contribution.
///     </para>
///     <para>
///         Takes <see cref="IHttpContextAccessor"/> because
///         <see cref="HeuristicDetector.DetectAsync"/> accepts a raw
///         <see cref="HttpContext"/> today. That contract is inherited from
///         the legacy contributor and stays put until the detector is itself
///         atomised.
///     </para>
///     <para>
///         No triggers -- heuristic runs for every request, including those
///         with missing UAs (a strong bot signal in itself). Config via
///         <see cref="IDetectorConfigProvider"/> under manifest name "Heuristic".
///     </para>
/// </remarks>
public sealed class HeuristicAtom : DetectorAtomBase
{
    private readonly ILogger<HeuristicAtom> _logger;
    private readonly HeuristicDetector _detector;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HeuristicAtom(
        ILogger<HeuristicAtom> logger,
        HeuristicDetector detector,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Heuristic", category: "HeuristicEarly")
    {
        _logger = logger;
        _detector = detector;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 50;

    private double HeuristicWeight =>
        _configProvider.GetParameter(Name, "heuristic_weight", 2.0);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return None();

        try
        {
            var result = await _detector.DetectAsync(context, ct);

            if (result.Reasons.Count == 0)
            {
                // Heuristic disabled or skipped
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = 0,
                    Weight = 0,
                    Reason = "Heuristic detection disabled or skipped"
                });
            }

            var reason = result.Reasons.First();
            var isBot = reason.ConfidenceImpact > 0;

            // Model 2 hints: signal name carries the value after the colon.
            sink.Raise($"{SignalKeys.HeuristicPrediction}:{(isBot ? "bot" : "human")}", sessionId);
            sink.Raise(
                $"{SignalKeys.HeuristicConfidence}:{result.Confidence.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}",
                sessionId);
            sink.Raise(SignalKeys.HeuristicEarlyCompleted, sessionId);

            // BotType=Unknown means HeuristicEarly couldn't infer a positive
            // type from features. Don't assert "Unknown" on the contribution --
            // the ledger BotType column is last-writer-wins, and an assertive
            // "Unknown" overwrites upstream atoms' authoritative types.
            var resolvedBotType = result.BotType is { } bt and not BotType.Unknown
                ? bt.ToString()
                : null;

            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = reason.ConfidenceImpact,
                Weight = HeuristicWeight,
                Reason = reason.Detail,
                BotType = resolvedBotType,
                BotName = result.BotName
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heuristic detection failed");
            return None();
        }
    }
}