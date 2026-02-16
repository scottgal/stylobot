using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     LLM (Ollama) contributor - emits an informational zero-weight contribution
///     indicating LLM background classification availability.
///     Actual LLM enqueue logic has moved to <see cref="BlackboardOrchestrator" />
///     which runs after detection completes and has access to AggregatedEvidence.
///
///     Configuration loaded from: llm.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:LlmContributor:*
/// </summary>
public class LlmContributor : ConfiguredContributorBase
{
    private readonly LlmClassificationCoordinator? _coordinator;
    private readonly ILogger<LlmContributor> _logger;
    private readonly BotDetectionOptions _options;

    public LlmContributor(
        ILogger<LlmContributor> logger,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider,
        LlmClassificationCoordinator? coordinator = null)
        : base(configProvider)
    {
        _logger = logger;
        _options = options.Value;
        _coordinator = coordinator;
    }

    public override string Name => "Llm";
    public override int Priority => Manifest?.Priority ?? 55;

    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(100);

    // Run when UserAgent signal exists (basic info available)
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.UserAgent)
    ];

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var isAvailable = _coordinator != null && _options.EnableLlmDetection;

        var reason = isAvailable
            ? "LLM background classification available"
            : "LLM detection disabled or unavailable";

        state.WriteSignal("llm.available", isAvailable);

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[]
        {
            new DetectionContribution
            {
                DetectorName = Name,
                Category = "AI",
                ConfidenceDelta = 0,
                Weight = 0,
                Reason = reason
            }
        });
    }
}
