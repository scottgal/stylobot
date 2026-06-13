using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;

// Same alias dance PolicyStackPresenter uses: the legacy enum at the parent
// namespace shadows the new PolicyAction record at .Rules under pattern-match.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the <see cref="PolicyEditRowViewModel"/> the C6 expression
///     editor renders. Pure read surface; never mutates a rule. The actual
///     write goes through the commercial mutation API (<c>/api/v1/policies</c>,
///     C3) -- the editor only formats the existing rule (or empty defaults)
///     into the shape <c>_EditRow.cshtml</c> wants.
/// </summary>
public sealed class PolicyEditPresenter
{
    /// <summary>
    ///     Default <see cref="RateLimitActionEdit.Key"/> when the legacy
    ///     <see cref="PolicyAction.RateLimit"/> record (which only carries
    ///     <c>RequestsPerMinute</c>) is widened into the editor model.
    ///     The richer fields land via the commercial JSONB sidecar later
    ///     in the traffic-shaping plan; this default keeps the editor
    ///     useful in the meantime.
    ///     <para>
    ///         <b>Vocabulary bridge:</b> <c>"fingerprint"</c> is the
    ///         editor-facing label. At enforcement time it maps to
    ///         <see cref="Mostlylucid.BotDetection.Actions.RateLimitKey.Signature"/>
    ///         on <see cref="Mostlylucid.BotDetection.Actions.RateLimitActionOptions.KeyBy"/>
    ///         (both bill against the primary signature; the editor uses the
    ///         operator-friendly word, runtime uses the registry-enum name).
    ///         The commercial wire-DTO mapper (traffic-shaping plan Task 2)
    ///         owns the translation in both directions -- editor labels
    ///         (<c>"fingerprint"</c> / <c>"ip"</c> / <c>"subnet"</c> /
    ///         <c>"asn"</c> / <c>"asn+signature"</c>) onto the
    ///         <see cref="Mostlylucid.BotDetection.Actions.RateLimitKey"/>
    ///         enum and back. Keep this string in sync with the mapper or
    ///         the legacy widening above will surface an unmappable label.
    ///     </para>
    /// </summary>
    private const string DefaultRateLimitKey = "fingerprint";

    /// <summary>
    ///     Default <see cref="RateLimitActionEdit.OverLimitAction"/>
    ///     surfaced when widening the legacy record. Matches the
    ///     traffic-shaping spec.
    ///     <para>
    ///         <b>No vocabulary bridge needed:</b> <c>"throttle-status"</c>
    ///         is the literal registered policy name in
    ///         <c>IActionPolicyRegistry</c> (see
    ///         <see cref="Mostlylucid.BotDetection.Actions.RateLimitActionOptions.OverLimitAction"/>
    ///         and the preset choices it documents -- <c>throttle-status</c>,
    ///         <c>block-soft</c>, <c>logonly</c>). Editor and runtime share
    ///         this vocabulary verbatim, so the Task 2 wire-DTO mapper is a
    ///         pass-through here.
    ///     </para>
    /// </summary>
    private const string DefaultOverLimitAction = "throttle-status";

    private readonly IPolicyRuleStore _ruleStore;

    public PolicyEditPresenter(IPolicyRuleStore ruleStore)
    {
        _ruleStore = ruleStore;
    }

    /// <summary>
    ///     Build the edit-row view model for an existing rule. The submit
    ///     path resolves to <c>PUT /api/v1/policies/{id}</c> so the
    ///     commercial mutation API does the right thing without the FOSS
    ///     editor caring whether a write store is even present.
    /// </summary>
    public async Task<PolicyEditRowViewModel?> BuildForExistingRuleAsync(
        Guid ruleId,
        CancellationToken ct = default)
    {
        var rule = await _ruleStore.GetByIdAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null) return null;

        var (tag, challenge, rateLimit, throttle, kind) = MapAction(rule.Action);

        return new PolicyEditRowViewModel(
            RuleId: rule.Id,
            Scope: rule.Scope,
            Priority: rule.Priority,
            PredicateText: PredicateFormatter.Format(rule.Predicate),
            ActionKind: kind,
            Tag: tag,
            Challenge: challenge,
            RateLimit: rateLimit,
            Throttle: throttle,
            Mode: rule.Mode,
            Notes: rule.Notes,
            SubmitUrl: $"/api/v1/policies/{rule.Id}",
            HttpMethod: "PUT",
            CancelUrl: $"/dashboard/policystack/rows?scope={PolicyScopeUrl.Encode(rule.Scope)}&tab=effective");
    }

    /// <summary>
    ///     Build the edit-row view model for a new rule at the given scope.
    ///     POSTs to <c>/api/v1/policies</c>.
    /// </summary>
    public PolicyEditRowViewModel BuildForNewRule(PolicyScope scope)
    {
        return new PolicyEditRowViewModel(
            RuleId: null,
            Scope: scope,
            Priority: 0,
            PredicateText: string.Empty,
            ActionKind: "observe",
            Tag: null,
            Challenge: null,
            RateLimit: null,
            Throttle: null,
            Mode: PolicyMode.Draft,
            Notes: string.Empty,
            SubmitUrl: "/api/v1/policies",
            HttpMethod: "POST",
            CancelUrl: $"/dashboard/policystack/rows?scope={PolicyScopeUrl.Encode(scope)}&tab=effective");
    }

    /// <summary>
    ///     Project a discriminated <see cref="PolicyAction"/> onto the
    ///     per-kind edit slices on <see cref="PolicyEditRowViewModel"/>.
    ///     Exactly one slice is non-null on return; the kind string is
    ///     always set and always lower-case.
    /// </summary>
    private static (TagActionEdit? Tag,
                    ChallengeActionEdit? Challenge,
                    RateLimitActionEdit? RateLimit,
                    ThrottleActionEdit? Throttle,
                    string Kind) MapAction(PolicyAction action) => action switch
    {
        RuleAction.Allow         => (null, null, null, null, "allow"),
        RuleAction.Observe       => (null, null, null, null, "observe"),
        RuleAction.Block         => (null, null, null, null, "block"),
        RuleAction.Tag t         => (new TagActionEdit(t.Name), null, null, null, "tag"),
        RuleAction.Challenge c   => (null, new ChallengeActionEdit(c.Kind), null, null, "challenge"),
        RuleAction.RateLimit rl  => (null, null,
            new RateLimitActionEdit(
                Rate: rl.RequestsPerMinute,
                Unit: "minute",
                Key: DefaultRateLimitKey,
                Burst: null,
                MitigationTimeoutSeconds: null,
                OverLimitAction: DefaultOverLimitAction),
            null, "ratelimit"),
        RuleAction.Throttle t    => (null, null, null,
            new ThrottleActionEdit(t.RequestsPerSecond, t.Reason), "throttle"),
        _ => throw new InvalidOperationException($"Unhandled action kind {action.GetType().Name}")
    };
}