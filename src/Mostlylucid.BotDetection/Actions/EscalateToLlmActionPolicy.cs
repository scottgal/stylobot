using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Escalator action policy that enqueues the request onto
///     <see cref="LlmClassificationCoordinator"/> for out-of-band LLM
///     classification. The visitor's response is unaffected; the LLM verdict
///     lands on the shared reputation cache + learning fabric asynchronously
///     via the coordinator's tick handler.
/// </summary>
/// <remarks>
///     <para>
///         <b>Distinct from the summary path.</b> This is the "full context"
///         escalator: unlike <see cref="EscalateToSessionActionPolicy"/>
///         which promotes a compact summary into the shared session store,
///         this escalator snapshots the whole HttpContext (all headers,
///         cookies with PII scrubbed, method / path / query, connection
///         metadata) plus the full aggregated evidence signal blackboard.
///         The LLM path needs everything because the classifier is being
///         asked to make a decision the deterministic pipeline could not.
///     </para>
///     <para>
///         Gated by an "uncertain band" (min/max bot probability) because LLM
///         calls are the most expensive escalation lane -- escalating on
///         confident verdicts (p&lt;0.15 or p&gt;0.85) burns tokens without
///         changing the outcome. Operators tune the band per site.
///     </para>
///     <para>
///         Snapshot at escalate time because
///         <see cref="Microsoft.AspNetCore.Http.HttpContext"/> is
///         per-request-scope and cannot be held across the tick-driven LLM
///         drain. Sensitive headers (Authorization, Cookie, Set-Cookie,
///         X-Api-Key) redact to <c>&lt;redacted&gt;</c>; cookie values
///         redact to just their name so shape is preserved for the LLM
///         while credentials are not.
///     </para>
///     <para>
///         When the shared LLM request sink is not registered (host without
///         the LLM lane), the policy no-ops and returns <c>Allowed</c>.
///         Callers routinely run without an LLM provider (FOSS default).
///     </para>
/// </remarks>
public sealed class EscalateToLlmActionPolicy : IActionPolicy
{
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<LlmClassificationRequest>? _requestSignals;
    private readonly ILogger<EscalateToLlmActionPolicy>? _logger;
    private readonly EscalateToLlmActionOptions _options;

    public EscalateToLlmActionPolicy(
        string name,
        EscalateToLlmActionOptions options,
        Mostlylucid.Ephemeral.TypedSignalSink<LlmClassificationRequest>? requestSignals = null,
        ILogger<EscalateToLlmActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestSignals = requestSignals;
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
        if (_requestSignals is null)
        {
            _logger?.LogDebug(
                "EscalateToLlm[{Name}] no-op: LLM request sink not registered",
                Name);
            return Task.FromResult(ActionResult.Allowed("Escalation skipped: LLM lane absent"));
        }

        if (evidence.BotProbability < _options.MinBotProbability ||
            evidence.BotProbability > _options.MaxBotProbability)
        {
            _logger?.LogDebug(
                "EscalateToLlm[{Name}] no-op: p={Prob:F2} outside uncertain band [{Min:F2},{Max:F2}]",
                Name, evidence.BotProbability, _options.MinBotProbability, _options.MaxBotProbability);
            return Task.FromResult(ActionResult.Allowed("Escalation skipped: outside uncertain band"));
        }

        var request = BuildRequest(context, evidence);

        // Raise onto the shared sink. The first raise fires
        // LlmClassificationSinkOptions.InitSignal, which lazy-boots the
        // coordinator; the coordinator's ctor drains via TypedSignalRaised
        // + Sense catch-up so this raise is never lost.
        _requestSignals.Raise(
            LlmClassificationCoordinator.RequestSignal.Name,
            request,
            key: request.RequestId);

        _logger?.LogInformation(
            "Escalated to LLM[{Name}]: {Signature} p={Prob:F2} reason={Reason}",
            Name, request.PrimarySignature, evidence.BotProbability, request.EnqueueReason);
        var enqueued = true;

        return Task.FromResult(ActionResult.Allowed(
            enqueued ? "Enqueued for LLM classification" : "LLM queue full"));
    }

    private LlmClassificationRequest BuildRequest(HttpContext context, AggregatedEvidence evidence)
    {
        var signature = ExtractSignature(context, evidence);
        var userAgent = context.Request.Headers.UserAgent.ToString();

        return new LlmClassificationRequest
        {
            RequestId = context.TraceIdentifier,
            PrimarySignature = signature,
            UserAgent = userAgent,
            PreBuiltRequestInfo = BuildRequestInfo(context, userAgent),
            HeuristicProbability = evidence.BotProbability,
            TopReasons = new List<string>(evidence.ContributingDetectors.Take(5)),
            Signals = evidence.Signals ?? new Dictionary<string, object>(),
            BotType = evidence.PrimaryBotType?.ToString(),
            BotName = evidence.PrimaryBotName,
            Path = context.Request.Path.Value,
            Method = context.Request.Method,
            Confidence = evidence.Confidence,
            RiskBand = evidence.RiskBand.ToString(),
            Action = Name,
            IsDriftSample = _options.IsDriftSample,
            IsConfirmationSample = false,
            EnqueueReason = _options.EnqueueReason,
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

    /// <summary>
    ///     Snapshot of the full HttpContext: request line, all headers
    ///     (sensitive redacted), cookies (values scrubbed), connection
    ///     metadata. Sits on <c>LlmClassificationRequest.PreBuiltRequestInfo</c>
    ///     and is what the LLM eventually sees when the coordinator drains
    ///     the queue -- HttpContext is per-request-scope and cannot be
    ///     held across the tick, so the snapshot is the whole story on the
    ///     LLM side.
    /// </summary>
    private static string BuildRequestInfo(HttpContext context, string userAgent)
    {
        var sb = new StringBuilder();
        var req = context.Request;

        sb.Append(req.Method).Append(' ').Append(req.Path).Append(req.QueryString).Append(' ').AppendLine(req.Protocol);
        sb.Append("Scheme: ").AppendLine(req.Scheme);
        if (req.Host.HasValue) sb.Append("Host: ").AppendLine(req.Host.Value);
        if (req.ContentType is not null) sb.Append("Content-Type: ").AppendLine(req.ContentType);
        if (req.ContentLength is { } len) sb.Append("Content-Length: ").AppendLine(len.ToString());
        sb.Append("User-Agent: ").AppendLine(userAgent);

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(remoteIp)) sb.Append("Remote-IP: ").AppendLine(remoteIp);
        var localIp = context.Connection.LocalIpAddress?.ToString();
        if (!string.IsNullOrEmpty(localIp)) sb.Append("Local-IP: ").AppendLine(localIp);

        sb.AppendLine();
        sb.AppendLine("Headers:");
        foreach (var (name, values) in req.Headers)
        {
            var value = IsSensitiveHeader(name)
                ? "<redacted>"
                : string.Join(", ", (string[])values!);
            sb.Append("  ").Append(name).Append(": ").AppendLine(value);
        }

        if (req.Cookies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Cookies (values scrubbed):");
            foreach (var cookieName in req.Cookies.Keys)
                sb.Append("  ").AppendLine(cookieName);
        }

        return sb.ToString();
    }

    private static bool IsSensitiveHeader(string name)
    {
        // Redact anything carrying credentials / session state. Names come
        // from HeaderDictionary keys so string comparison is enough.
        if (name.StartsWith("X-Api-Key", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);
    }
}