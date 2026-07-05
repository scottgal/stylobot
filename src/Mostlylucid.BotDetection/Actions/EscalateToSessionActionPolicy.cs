using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Summary-path escalator: builds a compact
///     <see cref="SessionSample"/> from the response, upserts it into the
///     shared per-domain <see cref="SessionStore"/>. Runs on every response
///     that matches the operator's YAML rule. Cheap.
/// </summary>
/// <remarks>
///     <para>
///         The escalator's only job is <b>promotion</b>: pick the small set
///         of fields that feed session-level fingerprint-shift detection and
///         emit them into the shared store. Everything not carried on the
///         sample stays request-scoped and dies with the request. The LLM
///         path (a different escalator) is what carries the full context
///         when it fires.
///     </para>
///     <para>
///         Identity resolution comes from the request cycle: the
///         fingerprint has already matched L1 → L2 → L3 by the time the
///         action pipeline runs, so we read the resolved ID from
///         <c>HttpContext.Items[SignalKeys.IdentityFingerprintId]</c> (or
///         fall back through <c>evidence.Signals</c>, then to the L1
///         signature as last resort). Site ID comes from
///         <see cref="ISiteProfileResolver"/>.
///     </para>
///     <para>
///         Coordinator-free: the escalator has no dependency on any
///         per-session or per-signature coordinator. Upsert → the shaped
///         eviction curve in <see cref="SessionStore"/> takes it from
///         there; <c>SessionAtom</c> observes the mutation on
///         <see cref="SessionStore.Changes"/> and drives persistence when
///         the aggregate shifts the fingerprint.
///     </para>
/// </remarks>
public sealed class EscalateToSessionActionPolicy : IActionPolicy
{
    private readonly SessionStore _store;
    private readonly ISiteProfileResolver? _siteProfiles;
    private readonly ILogger<EscalateToSessionActionPolicy>? _logger;
    private readonly EscalateToSessionActionOptions _options;

    public EscalateToSessionActionPolicy(
        string name,
        EscalateToSessionActionOptions options,
        SessionStore store,
        ISiteProfileResolver? siteProfiles = null,
        ILogger<EscalateToSessionActionPolicy>? logger = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _siteProfiles = siteProfiles;
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
        if (evidence.BotProbability < _options.MinBotProbability)
        {
            return Task.FromResult(ActionResult.Allowed("Escalation skipped: below threshold"));
        }

        var fingerprintId = ResolveFingerprintId(context, evidence);
        if (string.IsNullOrEmpty(fingerprintId))
        {
            _logger?.LogDebug(
                "EscalateToSession[{Name}] no-op: no fingerprint ID resolved for request",
                Name);
            return Task.FromResult(ActionResult.Allowed("Escalation skipped: no fingerprint"));
        }

        var siteId = ResolveSiteId(context);
        var fromUpstream = ReadBool(evidence.Signals, SignalKeys.ResponseFromUpstream, defaultValue: true);
        var honeypot = ReadBool(evidence.Signals, "request.honeypot");

        var sample = new SessionSample
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = evidence.BotProbability,
            Confidence = evidence.Confidence,
            StatusCode = context.Response.StatusCode,
            FromUpstream = fromUpstream,
            Honeypot = honeypot,
            Path = context.Request.Path.Value,
            ClientType = ExtractClientType(evidence),
            RequestId = context.TraceIdentifier,
        };

        var aggregate = _store.Upsert(sample);

        _logger?.LogDebug(
            "Escalated to session[{Name}]: fp={FingerprintId} site={SiteId} sampleCount={Count} mean={Mean:F2}",
            Name, fingerprintId, siteId, aggregate.SampleCount, aggregate.MeanBotProbability);

        return Task.FromResult(ActionResult.Allowed($"Session sample raised: fp={fingerprintId}"));
    }

    private static string? ResolveFingerprintId(HttpContext context, AggregatedEvidence evidence)
    {
        if (context.Items.TryGetValue(SignalKeys.IdentityFingerprintId, out var raw) &&
            raw is string s && !string.IsNullOrEmpty(s))
        {
            return s;
        }
        if (evidence.Signals is not null &&
            evidence.Signals.TryGetValue(SignalKeys.IdentityFingerprintId, out var evRaw) &&
            evRaw is string evStr && !string.IsNullOrEmpty(evStr))
        {
            return evStr;
        }
        if (evidence.Signals is not null &&
            evidence.Signals.TryGetValue("request.signature", out var sigRaw) &&
            sigRaw is string sigStr && !string.IsNullOrEmpty(sigStr))
        {
            return sigStr;
        }
        return null;
    }

    private string ResolveSiteId(HttpContext context)
    {
        if (_siteProfiles is null) return "default";
        var profile = _siteProfiles.Resolve(context);
        return profile?.Id ?? "default";
    }

    private static bool ReadBool(IReadOnlyDictionary<string, object>? signals, string key, bool defaultValue = false)
    {
        if (signals is null) return defaultValue;
        if (!signals.TryGetValue(key, out var raw) || raw is null) return defaultValue;
        return raw switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static string? ExtractClientType(AggregatedEvidence evidence)
    {
        if (evidence.PrimaryBotType is { } botType) return botType.ToString();
        return null;
    }
}