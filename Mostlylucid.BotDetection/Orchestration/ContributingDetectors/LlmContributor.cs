using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     LLM (Ollama) contributor - uses large language model for bot classification.
///     Runs in Wave 1+ after initial detectors have run and when AI escalation is triggered.
///     This is the most expensive detector and should only run when needed.
///
///     Configuration loaded from: llm.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:LlmContributor:*
/// </summary>
public class LlmContributor : ConfiguredContributorBase
{
    private readonly LlmDetector _detector;
    private readonly ILogger<LlmContributor> _logger;
    private readonly BotDetectionOptions _options;

    public LlmContributor(
        ILogger<LlmContributor> logger,
        LlmDetector detector,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
        _detector = detector;
        _options = options.Value;
    }

    public override string Name => "Llm";
    public override int Priority => Manifest?.Priority ?? 55;

    // Config-driven parameters from YAML
    private double LlmWeight => GetParam("llm_weight", 2.5);
    private int MaxTimeoutMs => GetParam("max_timeout_ms", 30000);

    // LLM needs longer timeout for model loading and inference (especially cold start)
    // Uses AiDetection.TimeoutMs from config (default 15000ms), with max from YAML
    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(
        Math.Min(_options.AiDetection.TimeoutMs * 2, MaxTimeoutMs));

    // Trigger when we have enough signals and want AI classification
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        // Run when UserAgent signal exists (basic info available)
        Triggers.WhenSignalExists(SignalKeys.UserAgent)
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var result = await _detector.DetectAsync(state.HttpContext, cancellationToken);

            if (result.Reasons.Count == 0)
            {
                // LLM disabled or skipped
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AI",
                    ConfidenceDelta = 0,
                    Weight = 0,
                    Reason = "LLM detection disabled or skipped",
                    Signals = ImmutableDictionary<string, object>.Empty
                });
            }
            else
            {
                // LLM made a prediction (use reason's ConfidenceImpact which is negative for human)
                var reason = result.Reasons.First();
                var isBot = reason.ConfidenceImpact > 0;

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "AI",
                    ConfidenceDelta = reason.ConfidenceImpact,
                    Weight = LlmWeight, // LLM predictions are weighted very heavily
                    Reason = reason.Detail,
                    BotType = result.BotType?.ToString(),
                    BotName = result.BotName,
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.AiPrediction, isBot ? "bot" : "human")
                        .Add(SignalKeys.AiConfidence, result.Confidence)
                        .Add(SignalKeys.AiLearnedPattern, reason.Detail)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM detection failed");
        }

        return contributions;
    }
}