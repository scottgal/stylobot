using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     LLM availability SensorAtom (per Taxonomy.md) — raises
///     <c>llm.available</c> on the sink to signal downstream atoms whether the
///     LLM background classification path is wired for this host. Zero-weight
///     informational contribution mirrors the legacy behaviour.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>LlmContributor</c>. Delegates ILlmProvider discovery to
///         <see cref="IServiceProvider"/> so plugin packages can wire their own
///         provider without a compile-time dependency here.
///     </para>
///     <para>
///         Priority 55 matches the legacy contributor's Wave-1 slot; required
///         signal is <see cref="SignalKeys.UserAgent"/>.
///     </para>
/// </remarks>
public sealed class LlmAtom : DetectorAtomBase
{
    private readonly IServiceProvider _serviceProvider;
    private readonly LlmClassificationCoordinator? _coordinator;
    private readonly ILogger<LlmAtom> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IDetectorConfigProvider _configProvider;

    public LlmAtom(
        ILogger<LlmAtom> logger,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider,
        IServiceProvider serviceProvider,
        LlmClassificationCoordinator? coordinator = null)
        : base(name: "Llm", category: "AI")
    {
        _logger = logger;
        _options = options.Value;
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
        _configProvider = configProvider;
    }

    public override int Priority => 55;
    public override TimeSpan Timeout => TimeSpan.FromMilliseconds(100);
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        // Check if an ILlmProvider is registered (from Llm plugin packages).
        var llmProviderType = Type.GetType("Mostlylucid.BotDetection.Llm.ILlmProvider, Mostlylucid.BotDetection.Llm");
        var hasProvider = llmProviderType != null && _serviceProvider.GetService(llmProviderType) != null;

#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        var isAvailable = (_coordinator != null || hasProvider) && _options.EnableLlmDetection;
#pragma warning restore CS0618

        var reason = isAvailable
            ? "LLM background classification available"
            : "LLM detection disabled or unavailable";

        if (isAvailable)
            sink.Raise("llm.available", sessionId);

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = Name,
            Category = Category,
            ConfidenceDelta = 0,
            Weight = 0,
            Reason = reason
        }));
    }
}
