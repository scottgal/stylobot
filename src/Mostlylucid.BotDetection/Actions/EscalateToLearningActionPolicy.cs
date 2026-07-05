using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Learning;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Escalator action policy that raises a <see cref="LearningEvent"/>
///     directly onto the shared <see cref="TypedSignalSink{LearningEvent}"/>.
///     The event travels the typed sink and lands on every subscribed
///     handler (drift, reputation, HNSW, anomaly saver, etc.) without any
///     request-thread latency cost beyond the sink raise.
/// </summary>
/// <remarks>
///     <para>
///         The escalator has no coordinator dependency by design: the sink
///         is boot-time infrastructure that always exists, coordinators are
///         lazy consumers that StyloFlow's start-signal primitive will boot
///         on first raise (identifier
///         <see cref="LearningSignalSinkOptions.InitSignal"/>). Escalators
///         raise; the fabric wakes up.
///     </para>
///     <para>
///         Configuration is threshold-driven (see
///         <see cref="EscalateToLearningActionOptions"/>): the policy
///         no-ops when bot probability or confidence is below the operator
///         floor, so escalating YAML rules can be scoped to strong verdicts
///         without introducing a bespoke gate in the rule itself.
///     </para>
/// </remarks>
public sealed class EscalateToLearningActionPolicy : IActionPolicy
{
    private readonly TypedSignalSink<LearningEvent> _signals;
    private readonly ILogger<EscalateToLearningActionPolicy>? _logger;
    private readonly EscalateToLearningActionOptions _options;

    public EscalateToLearningActionPolicy(
        string name,
        EscalateToLearningActionOptions options,
        TypedSignalSink<LearningEvent> signals,
        ILogger<EscalateToLearningActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _signals = signals ?? throw new ArgumentNullException(nameof(signals));
        _logger = logger;
    }

    public string Name { get; }

    public ActionType ActionType => ActionType.Escalate;

    public PolicyIntent Intent => PolicyIntent.Escalate;

    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        if (evidence.BotProbability < _options.MinBotProbability ||
            evidence.Confidence < _options.MinConfidence)
        {
            _logger?.LogDebug(
                "EscalateToLearning[{Name}] no-op: below threshold (p={Prob:F2}, conf={Conf:F2})",
                Name, evidence.BotProbability, evidence.Confidence);
            return Task.FromResult(ActionResult.Allowed("Escalation skipped: below threshold"));
        }

        var learningEvent = BuildLearningEvent(context, evidence);
        var key = LearningSignalKeys.For(learningEvent.Type);
        _signals.Raise(key.Name, learningEvent, key: learningEvent.RequestId ?? learningEvent.Pattern ?? learningEvent.Source);

        _logger?.LogInformation(
            "Escalated to learning[{Name}]: {Type} for {Signature} (p={Prob:F2}, conf={Conf:F2})",
            Name, learningEvent.Type, learningEvent.Pattern, evidence.BotProbability, evidence.Confidence);

        return Task.FromResult(ActionResult.Allowed($"Escalated to learning: {learningEvent.Type}"));
    }

    private LearningEvent BuildLearningEvent(HttpContext context, AggregatedEvidence evidence)
    {
        var signature = ExtractSignature(context, evidence);
        var metadata = new Dictionary<string, object>
        {
            ["botProbability"] = evidence.BotProbability,
            ["confidence"] = evidence.Confidence,
            ["riskBand"] = evidence.RiskBand.ToString(),
            ["path"] = context.Request.Path.Value ?? "/",
            ["method"] = context.Request.Method,
        };

        var userAgent = context.Request.Headers.UserAgent.ToString();
        if (!string.IsNullOrEmpty(userAgent)) metadata["userAgent"] = userAgent;

        var ip = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(ip)) metadata["ip"] = ip;

        if (evidence.PrimaryBotType is { } botType) metadata["botType"] = botType.ToString();
        if (!string.IsNullOrEmpty(evidence.PrimaryBotName)) metadata["botName"] = evidence.PrimaryBotName;

        return new LearningEvent
        {
            Type = _options.EventType,
            Source = Name,
            Confidence = evidence.Confidence,
            Label = evidence.BotProbability >= 0.5,
            Pattern = signature,
            RequestId = context.TraceIdentifier,
            Features = _options.IncludeFeatureVector ? ExtractFeatures(evidence) : null,
            Metadata = metadata,
        };
    }

    private static string ExtractSignature(HttpContext context, AggregatedEvidence evidence)
    {
        if (evidence.Signals is not null &&
            evidence.Signals.TryGetValue("request.signature", out var raw) &&
            raw is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }
        return context.Request.Headers.UserAgent.ToString();
    }

    private static Dictionary<string, double> ExtractFeatures(AggregatedEvidence evidence)
    {
        var features = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["result:bot_probability"] = evidence.BotProbability,
            ["result:confidence"] = evidence.Confidence,
        };
        if (evidence.Signals is null) return features;
        foreach (var (key, value) in evidence.Signals)
        {
            switch (value)
            {
                case double d: features[key] = d; break;
                case float f: features[key] = f; break;
                case int i: features[key] = i; break;
                case long l: features[key] = l; break;
                case bool b: features[key] = b ? 1.0 : 0.0; break;
            }
        }
        return features;
    }
}