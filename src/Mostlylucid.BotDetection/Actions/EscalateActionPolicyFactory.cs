using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.SiteProfiles;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Factory for the three "escalate to X" action policies.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ActionPolicyRegistry"/> keys factories by
///         <see cref="IActionPolicyFactory.ActionType"/> (one factory per
///         type — <c>_factories.ToDictionary(f => f.ActionType)</c>). The
///         three escalate policies all share <see cref="ActionType.Escalate"/>,
///         so we cannot register a separate factory per policy class. One
///         combined factory reads <c>options["Target"]</c> to pick the
///         concrete class: <c>learning</c> (default) →
///         <see cref="EscalateToLearningActionPolicy"/>, <c>session</c> →
///         <see cref="EscalateToSessionActionPolicy"/>, <c>llm</c> →
///         <see cref="EscalateToLlmActionPolicy"/>.
///     </para>
///     <para>
///         Escalate-to-Session and Escalate-to-LLM take deps that a bare FOSS
///         host may not have wired (session store, LLM request sink). Both
///         are pulled via constructor injection with a nullable fallback so
///         hosts that never configure the option can still boot — the
///         escalate-session / escalate-llm code paths simply never
///         instantiate.
///     </para>
///     <para>
///         See <c>EscalateActionOptions.cs</c> for the per-variant option
///         classes (<see cref="EscalateToLearningActionOptions"/>,
///         <see cref="EscalateToSessionActionOptions"/>,
///         <see cref="EscalateToLlmActionOptions"/>).
///     </para>
/// </remarks>
public sealed class EscalateActionPolicyFactory : IActionPolicyFactory
{
    private readonly TypedSignalSink<LearningEvent> _learningSignals;
    private readonly SessionStore? _sessionStore;
    private readonly ISiteProfileResolver? _siteProfiles;
    private readonly TypedSignalSink<LlmClassificationRequest>? _llmRequestSignals;
    private readonly ILogger<EscalateToLearningActionPolicy>? _learningLogger;
    private readonly ILogger<EscalateToSessionActionPolicy>? _sessionLogger;
    private readonly ILogger<EscalateToLlmActionPolicy>? _llmLogger;

    public EscalateActionPolicyFactory(
        TypedSignalSink<LearningEvent> learningSignals,
        SessionStore? sessionStore = null,
        ISiteProfileResolver? siteProfiles = null,
        TypedSignalSink<LlmClassificationRequest>? llmRequestSignals = null,
        ILogger<EscalateToLearningActionPolicy>? learningLogger = null,
        ILogger<EscalateToSessionActionPolicy>? sessionLogger = null,
        ILogger<EscalateToLlmActionPolicy>? llmLogger = null)
    {
        _learningSignals = learningSignals ?? throw new ArgumentNullException(nameof(learningSignals));
        _sessionStore = sessionStore;
        _siteProfiles = siteProfiles;
        _llmRequestSignals = llmRequestSignals;
        _learningLogger = learningLogger;
        _sessionLogger = sessionLogger;
        _llmLogger = llmLogger;
    }

    public ActionType ActionType => ActionType.Escalate;

    public IActionPolicy Create(string name, IDictionary<string, object> options)
    {
        var target = options.TryGetValue("Target", out var targetRaw)
            ? targetRaw?.ToString()?.Trim().ToLowerInvariant() ?? "learning"
            : "learning";

        return target switch
        {
            "learning" => CreateLearning(name, options),
            "session" => CreateSession(name, options),
            "llm" => CreateLlm(name, options),
            _ => throw new ArgumentException(
                $"Unknown escalate Target '{target}' for policy '{name}'. " +
                "Valid values: learning, session, llm.")
        };
    }

    private IActionPolicy CreateLearning(string name, IDictionary<string, object> options)
    {
        var learningOptions = new EscalateToLearningActionOptions();

        if (options.TryGetValue("EventType", out var eventType) && eventType is not null)
        {
            if (Enum.TryParse<LearningEventType>(eventType.ToString(), ignoreCase: true, out var parsed))
                learningOptions.EventType = parsed;
        }

        if (options.TryGetValue("MinBotProbability", out var minP))
            learningOptions.MinBotProbability = Convert.ToDouble(minP);

        if (options.TryGetValue("MinConfidence", out var minC))
            learningOptions.MinConfidence = Convert.ToDouble(minC);

        if (options.TryGetValue("IncludeFeatureVector", out var incF))
            learningOptions.IncludeFeatureVector = Convert.ToBoolean(incF);

        return new EscalateToLearningActionPolicy(
            name, learningOptions, _learningSignals, _learningLogger);
    }

    private IActionPolicy CreateSession(string name, IDictionary<string, object> options)
    {
        if (_sessionStore is null)
        {
            throw new InvalidOperationException(
                $"Escalate policy '{name}' targets 'session' but no SessionStore is registered. " +
                "Wire AddBotDetectionModule (which registers SessionStore) or provide a custom registration.");
        }

        var sessionOptions = new EscalateToSessionActionOptions();

        if (options.TryGetValue("MinBotProbability", out var minP))
            sessionOptions.MinBotProbability = Convert.ToDouble(minP);

        return new EscalateToSessionActionPolicy(
            name, sessionOptions, _sessionStore, _siteProfiles, _sessionLogger);
    }

    private IActionPolicy CreateLlm(string name, IDictionary<string, object> options)
    {
        var llmOptions = new EscalateToLlmActionOptions();

        if (options.TryGetValue("EnqueueReason", out var reason) && reason is not null)
            llmOptions.EnqueueReason = reason.ToString() ?? llmOptions.EnqueueReason;

        if (options.TryGetValue("MinBotProbability", out var minP))
            llmOptions.MinBotProbability = Convert.ToDouble(minP);

        if (options.TryGetValue("MaxBotProbability", out var maxP))
            llmOptions.MaxBotProbability = Convert.ToDouble(maxP);

        if (options.TryGetValue("IsDriftSample", out var drift))
            llmOptions.IsDriftSample = Convert.ToBoolean(drift);

        return new EscalateToLlmActionPolicy(
            name, llmOptions, _llmRequestSignals, _llmLogger);
    }
}