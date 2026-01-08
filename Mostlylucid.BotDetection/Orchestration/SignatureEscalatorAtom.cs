using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Escalation;
using Mostlylucid.BotDetection.Orchestration.SignalMatching;
using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Escalator ATOM that listens for analysis.complete signals and decides:
///     - Should we escalate to signature coordinator?
///     - Should we store operation summary?
///     - Should we emit alerts?
///     - What priority for signature processing?
///     This atom is TIGHT with the OperationSignalSink (dies when sink dies).
///     Uses PATTERN MATCHING for dynamic signal resolution.
/// </summary>
public sealed class SignatureEscalatorAtom : IAsyncDisposable
{
    private readonly EscalatorConfig _config;
    private readonly ILogger<SignatureEscalatorAtom> _logger;
    private readonly SignalSink _operationSink; // Listen for analysis.complete
    private readonly string _requestId;

    // Pattern matchers for dynamic signal resolution
    private readonly SignalPatternMatcher _requestPatterns;
    private readonly SignalPatternMatcher _responsePatterns;
    private readonly string _signature;
    private readonly SignatureResponseCoordinatorCache _signatureCoordinators;
    private readonly SignalPatternMatcher _triggerPatterns;

    public SignatureEscalatorAtom(
        SignalSink operationSink,
        string signature,
        string requestId,
        SignatureResponseCoordinatorCache signatureCoordinators,
        ILogger<SignatureEscalatorAtom> logger,
        EscalatorConfig? config = null)
    {
        _operationSink = operationSink;
        _signature = signature;
        _requestId = requestId;
        _signatureCoordinators = signatureCoordinators;
        _logger = logger;
        _config = config ?? new EscalatorConfig();

        // Initialize pattern matchers from config
        _requestPatterns = new SignalPatternMatcher(_config.RequestPatterns);
        _responsePatterns = new SignalPatternMatcher(_config.ResponsePatterns);
        _triggerPatterns = new SignalPatternMatcher(_config.TriggerPatterns);
    }

    public async ValueTask DisposeAsync()
    {
        // Atom disposes when operation sink disposes
        _logger.LogDebug("SignatureEscalatorAtom disposed for {Signature}", _signature);
        await ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Called when request analysis completes.
    ///     Decides if early escalation needed using PATTERN MATCHING.
    /// </summary>
    public async Task OnRequestAnalysisCompleteAsync(CancellationToken cancellationToken = default)
    {
        // Use pattern matcher to extract signals dynamically
        var signals = ExtractSignals(_requestPatterns);

        // Get risk and honeypot from matched patterns (cast from object)
        var risk = signals.TryGetValue("risk", out var riskObj) && riskObj is double r ? r : 0.0;
        var honeypot = signals.TryGetValue("honeypot", out var honeypotObj) && honeypotObj is bool h ? h : false;

        // Decide: Early escalation?
        var decision = DecideEarlyEscalation(risk, honeypot, signals);

        if (decision.ShouldEscalate) await EscalateRequestAsync(decision, signals, cancellationToken);

        _logger.LogDebug(
            "Request analysis complete for {Signature}: escalate={Escalate}, reason={Reason}",
            _signature, decision.ShouldEscalate, decision.Reason);
    }

    /// <summary>
    ///     Called when response analysis completes (operation complete).
    ///     Decides final escalation, storage, alerts using PATTERN MATCHING.
    /// </summary>
    public async Task OnOperationCompleteAsync(CancellationToken cancellationToken = default)
    {
        // Extract signals using pattern matchers
        var requestSignals = ExtractSignals(_requestPatterns);
        var responseSignals = ExtractSignals(_responsePatterns);
        var allSignals = requestSignals.Concat(responseSignals).ToDictionary(k => k.Key, v => v.Value);

        // Get key signals from patterns (cast from object)
        var requestRisk = requestSignals.TryGetValue("risk", out var riskObj) && riskObj is double rr ? rr : 0.0;
        var responseScore = responseSignals.TryGetValue("score", out var scoreObj) && scoreObj is double rs ? rs : 0.0;
        var honeypot = requestSignals.TryGetValue("honeypot", out var hpObj) && hpObj is bool hp ? hp : false;
        var statusCode = responseSignals.TryGetValue("status", out var statusObj) && statusObj is int sc ? sc : 0;

        // Decide: Escalation, storage, alerts
        var decision = DecideOperationEscalation(requestRisk, responseScore, honeypot, statusCode, allSignals);

        // Always escalate operation complete (at minimum for window tracking)
        await EscalateOperationAsync(decision, allSignals, cancellationToken);

        // Storage decision
        if (decision.ShouldStore) await StoreOperationAsync(decision, allSignals, cancellationToken);

        // Alert decision
        if (decision.ShouldAlert) await EmitAlertAsync(decision, allSignals, cancellationToken);

        _logger.LogInformation(
            "Operation complete for {Signature}: priority={Priority}, store={Store}, alert={Alert}",
            _signature, decision.Priority, decision.ShouldStore, decision.ShouldAlert);
    }

    /// <summary>
    ///     Decide if early escalation is needed after request analysis.
    ///     Uses pattern-matched signals for dynamic decision making.
    /// </summary>
    private EscalationDecision DecideEarlyEscalation(double risk, bool honeypot, Dictionary<string, object> signals)
    {
        // Apply escalation rules from config (pattern-driven)
        foreach (var rule in _config.EscalationRules.OrderByDescending(r => r.Priority))
            if (rule.ShouldEscalate(signals))
                return new EscalationDecision
                {
                    ShouldEscalate = true,
                    Priority = rule.Priority,
                    Reason = rule.BuildReason(signals),
                    ShouldStore = rule.ShouldStore,
                    ShouldAlert = rule.ShouldAlert
                };

        // Default: No early escalation
        return new EscalationDecision
        {
            ShouldEscalate = false,
            Priority = 0,
            Reason = "No early escalation rules matched"
        };
    }

    /// <summary>
    ///     Decide escalation after operation completes.
    ///     Uses pattern-matched signals for dynamic decision making.
    /// </summary>
    private EscalationDecision DecideOperationEscalation(
        double requestRisk,
        double responseScore,
        bool honeypot,
        int statusCode,
        Dictionary<string, object> allSignals)
    {
        var combinedScore = Math.Max(requestRisk, responseScore);

        // Apply operation escalation rules from config
        foreach (var rule in _config.OperationEscalationRules.OrderByDescending(r => r.Priority))
            if (rule.ShouldEscalate(allSignals))
                return new EscalationDecision
                {
                    ShouldEscalate = true,
                    Priority = rule.Priority,
                    Reason = rule.BuildReason(allSignals),
                    ShouldStore = rule.ShouldStore,
                    ShouldAlert = rule.ShouldAlert
                };

        // Default: Always escalate for window tracking
        return new EscalationDecision
        {
            ShouldEscalate = true,
            Priority = (int)(combinedScore * 100),
            Reason = $"Operation complete: score={combinedScore:F2}",
            ShouldStore = combinedScore > _config.StoreThreshold,
            ShouldAlert = combinedScore > _config.AlertThreshold
        };
    }

    /// <summary>
    ///     Determine priority for signature coordinator processing.
    /// </summary>
    private int DeterminePriority(double combinedScore, bool honeypot, int statusCode)
    {
        if (honeypot) return 100;
        if (combinedScore > 0.9) return 90;
        if (combinedScore > 0.7) return 70;
        if (statusCode == 404) return 50;
        if (combinedScore > 0.5) return 40;
        return 10;
    }

    /// <summary>
    ///     Extract signals using a pattern matcher from the operation sink.
    /// </summary>
    private Dictionary<string, object> ExtractSignals(SignalPatternMatcher matcher)
    {
        return matcher.ExtractFrom(_operationSink);
    }

    /// <summary>
    ///     Execute early escalation to signature coordinator.
    /// </summary>
    private async Task EscalateRequestAsync(
        EscalationDecision decision,
        Dictionary<string, object> signals,
        CancellationToken cancellationToken)
    {
        var requestSignal = new RequestCompleteSignal
        {
            Signature = _signature,
            RequestId = _requestId,
            Timestamp = DateTimeOffset.UtcNow,
            Risk = GetSignal<double>("request.risk"),
            Honeypot = GetSignal<bool>("request.honeypot"),
            Datacenter = GetSignal<string>("request.ip.datacenter"),
            Path = GetSignal<string>("request.path"),
            Method = GetSignal<string>("request.method"),
            TriggerSignals = ExtractTriggerSignals("request.*")
        };

        var coordinator = await _signatureCoordinators.GetOrCreateAsync(_signature, cancellationToken);
        await coordinator.ReceiveRequestAsync(requestSignal, cancellationToken);

        _logger.LogInformation(
            "Early escalation: {Signature} → priority={Priority}, reason={Reason}",
            _signature, decision.Priority, decision.Reason);
    }

    /// <summary>
    ///     Execute operation escalation to signature coordinator.
    /// </summary>
    private async Task EscalateOperationAsync(
        EscalationDecision decision,
        Dictionary<string, object> allSignals,
        CancellationToken cancellationToken)
    {
        var operationSignal = new OperationCompleteSignal
        {
            Signature = _signature,
            RequestId = _requestId,
            Timestamp = DateTimeOffset.UtcNow,
            Priority = decision.Priority,

            // Request data
            RequestRisk = GetSignal<double>("request.risk"),
            Path = GetSignal<string>("request.path"),
            Method = GetSignal<string>("request.method"),

            // Response data
            ResponseScore = GetSignal<double>("response.score"),
            StatusCode = GetSignal<int>("response.status"),
            ResponseBytes = GetSignal<long>("response.bytes"),

            // Combined
            CombinedScore = decision.Priority / 100.0,
            Honeypot = GetSignal<bool>("request.honeypot"),
            Datacenter = GetSignal<string>("request.ip.datacenter"),

            // All trigger signals
            TriggerSignals = ExtractTriggerSignals("*")
        };

        var coordinator = await _signatureCoordinators.GetOrCreateAsync(_signature, cancellationToken);
        await coordinator.ReceiveOperationAsync(operationSignal, cancellationToken);
    }

    /// <summary>
    ///     Store operation summary (future: to database, logs, etc.)
    /// </summary>
    private async Task StoreOperationAsync(
        EscalationDecision decision,
        Dictionary<string, object> allSignals,
        CancellationToken cancellationToken)
    {
        // TODO: Implement storage
        // - Database write
        // - Log aggregation
        // - S3/blob storage
        _logger.LogDebug("Storage decision for {Signature}: {Reason}", _signature, decision.Reason);
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Emit alert signal (future: webhook, Slack, PagerDuty, etc.)
    /// </summary>
    private async Task EmitAlertAsync(
        EscalationDecision decision,
        Dictionary<string, object> allSignals,
        CancellationToken cancellationToken)
    {
        // TODO: Implement alerting
        // - Webhook
        // - Email
        // - Slack/Teams
        // - PagerDuty
        _logger.LogWarning("ALERT for {Signature}: {Reason}", _signature, decision.Reason);
        await Task.CompletedTask;
    }

    /// <summary>
    ///     Extract trigger signals from operation sink.
    ///     Pattern matching: "request.*" matches "request.risk", "request.honeypot", etc.
    /// </summary>
    private Dictionary<string, object> ExtractTriggerSignals(string pattern)
    {
        var signals = new Dictionary<string, object>();

        // Ephemeral 1.6.8: Sense takes predicate, signals contain data in Key property
        // Pattern matching: convert "request.*" to predicate
        var events = _operationSink.Sense(evt =>
            pattern == "*" || evt.Signal.StartsWith(pattern.Replace("*", "")));

        foreach (var evt in events.OrderByDescending(e => e.Timestamp))
            if (!signals.ContainsKey(evt.Signal))
                // In ephemeral 1.6.8, the value is in the Key property (second param of Raise)
                signals[evt.Signal] = evt.Key ?? evt.Signal;

        return signals;
    }

    /// <summary>
    ///     Get latest signal value from operation sink.
    ///     Returns the Key property which contains the value (from Raise(signal, value))
    /// </summary>
    private T GetSignal<T>(string signalName, T defaultValue = default)
    {
        var events = _operationSink.Sense(evt => evt.Signal == signalName);
        var latest = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();

        // Try to parse the Key as T
        if (latest == default || latest.Key == null)
            return defaultValue;

        try
        {
            if (typeof(T) == typeof(string))
                return (T)(object)latest.Key;
            if (typeof(T) == typeof(double))
                return double.TryParse(latest.Key, out var d) ? (T)(object)d : defaultValue;
            if (typeof(T) == typeof(int))
                return int.TryParse(latest.Key, out var i) ? (T)(object)i : defaultValue;
            if (typeof(T) == typeof(long))
                return long.TryParse(latest.Key, out var l) ? (T)(object)l : defaultValue;
            if (typeof(T) == typeof(bool))
                return bool.TryParse(latest.Key, out var b) ? (T)(object)b : defaultValue;

            return defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }
}