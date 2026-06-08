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

        var (kind, challengeKind, tagName, rpm) = ActionTriple(rule.Action);

        return new PolicyEditRowViewModel(
            RuleId: rule.Id,
            Scope: rule.Scope,
            Priority: rule.Priority,
            PredicateText: PredicateFormatter.Format(rule.Predicate),
            ActionKind: kind,
            ChallengeKind: challengeKind,
            TagName: tagName,
            RequestsPerMinute: rpm,
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
            ChallengeKind: null,
            TagName: null,
            RequestsPerMinute: null,
            Mode: PolicyMode.Draft,
            Notes: string.Empty,
            SubmitUrl: "/api/v1/policies",
            HttpMethod: "POST",
            CancelUrl: $"/dashboard/policystack/rows?scope={PolicyScopeUrl.Encode(scope)}&tab=effective");
    }

    private static (string kind, string? challenge, string? tagName, int? rpm) ActionTriple(PolicyAction action) =>
        action switch
        {
            RuleAction.Allow => ("allow", null, null, null),
            RuleAction.Observe => ("observe", null, null, null),
            RuleAction.Tag t => ("tag", null, t.Name, null),
            RuleAction.Challenge c => ("challenge", c.Kind, null, null),
            RuleAction.RateLimit r => ("ratelimit", null, null, r.RequestsPerMinute),
            RuleAction.Block => ("block", null, null, null),
            _ => ("observe", null, null, null)
        };
}
