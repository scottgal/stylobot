using System.Globalization;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Configuration;
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
    // `internal` (was `private`) so the action-editor query-string ->
    // model construction in
    // <see cref="Mostlylucid.BotDetection.UI.Policies.PolicyActionEditorViewPaths.BuildActionEditorModel"/>
    // can reuse the same default literal without re-declaring it. Same
    // string in two places is the duplication smell `feedback_no_duplication`
    // calls out.
    internal const string DefaultRateLimitKey = "fingerprint";

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
    // `internal` (was `private`) -- same reuse rationale as
    // <see cref="DefaultRateLimitKey"/>: the action-editor model builder
    // reaches in here so the spec default lives in exactly one place.
    internal const string DefaultOverLimitAction = "throttle-status";

    private readonly IPolicyRuleStore _ruleStore;
    private readonly IOptions<PolicyEditTimingsOptions> _timings;
    private readonly IDashboardLinkResolver? _links;
    private readonly IFacetPickerCatalog? _facetCatalog;

    public PolicyEditPresenter(
        IPolicyRuleStore ruleStore,
        IOptions<PolicyEditTimingsOptions>? timings = null,
        IDashboardLinkResolver? links = null,
        IFacetPickerCatalog? facetCatalog = null)
    {
        _ruleStore = ruleStore;
        // Composition-light hosts (tests, AOT bundles) may skip binding the
        // timings options; fall back to the FOSS-literal defaults so the
        // presenter is always self-sufficient.
        _timings = timings ?? Microsoft.Extensions.Options.Options.Create(new PolicyEditTimingsOptions());
        _links = links;
        // Catalog is optional so AOT bundles + tests that don't wire the
        // picker still build the inner editor VM cleanly. Hosts that mount
        // the expand surface always register the catalog (singleton).
        _facetCatalog = facetCatalog;
    }

    private string CancelUrlFor(PolicyScope scope)
    {
        var sub = $"/policystack/rows?scope={PolicyScopeUrl.Encode(scope)}&tab=effective";
        return _links?.Resolve(sub) ?? "/stylobot" + sub;
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

        var timings = _timings.Value;
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
            CancelUrl: CancelUrlFor(rule.Scope),
            Backtest: null,
            ParseDebounceMs: timings.ParseDebounceMs,
            BacktestDebounceMs: timings.BacktestDebounceMs);
    }

    /// <summary>
    ///     Build the edit-row view model for a new rule at the given scope.
    ///     POSTs to <c>/api/v1/policies</c>.
    /// </summary>
    public PolicyEditRowViewModel BuildForNewRule(PolicyScope scope)
    {
        var timings = _timings.Value;
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
            CancelUrl: CancelUrlFor(scope),
            Backtest: null,
            ParseDebounceMs: timings.ParseDebounceMs,
            BacktestDebounceMs: timings.BacktestDebounceMs);
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

    // ─── B8: card-shaped expand surface ────────────────────────────────────
    //
    // The expand VM WRAPS the existing PolicyEditRowViewModel rather than
    // duplicating its fields. _RuleCardExpand.cshtml renders the card chrome
    // (header / picker / hysteresis disclosure) and delegates the predicate
    // editor + per-kind action editor to the existing _EditRow embed -- so
    // the canonical form-encoding (action.kind / predicate / mode / notes)
    // and all the JS the existing editor already drives keep working
    // unchanged. The wire path is still PUT /api/v1/policies/{id}.

    /// <summary>
    ///     Build the card-shaped expand VM for an existing rule. Returns
    ///     <c>null</c> when the rule id is unknown so the caller can map to
    ///     a 404 cleanly.
    /// </summary>
    public async Task<RuleCardExpandViewModel?> BuildExpandForExistingRuleAsync(
        Guid ruleId,
        CancellationToken ct = default)
    {
        var rule = await _ruleStore.GetByIdAsync(ruleId, ct).ConfigureAwait(false);
        if (rule is null) return null;

        var inner = await BuildForExistingRuleAsync(ruleId, ct).ConfigureAwait(false);
        if (inner is null) return null;

        var rows = BuildPickerRows(rule.Predicate);
        return new RuleCardExpandViewModel(
            Editor: inner,
            ScopeTarget: PolicyScopeFormatter.ToHeadline(rule.Scope),
            Action: rule.Action,
            AutoPromoteAt: rule.AutoPromoteAt,
            Trigger: rule.Trigger,
            PickerRows: rows);
    }

    /// <summary>
    ///     Build the card-shaped expand VM for a new rule at the given scope.
    ///     Picker rows start empty -- the operator either picks a facet or
    ///     types directly into the raw-DSL textarea.
    /// </summary>
    public RuleCardExpandViewModel BuildExpandForNewRule(PolicyScope scope)
    {
        var inner = BuildForNewRule(scope);
        return new RuleCardExpandViewModel(
            Editor: inner,
            ScopeTarget: PolicyScopeFormatter.ToHeadline(scope),
            Action: null,
            AutoPromoteAt: null,
            Trigger: null,
            PickerRows: Array.Empty<FacetPickerRowViewModel>());
    }

    /// <summary>
    ///     Project the rule's predicate AST onto a flat list of picker rows
    ///     when (and only when) the AST is shaped as a single
    ///     <see cref="Predicate.Term"/> or an <see cref="Predicate.And"/>
    ///     whose children are all <c>Term</c>s. Mixed AND/OR predicates and
    ///     nested groupings return an empty list -- the raw-DSL textarea
    ///     still carries the full predicate, and the picker just doesn't
    ///     pre-populate. Picker rows that reference a facet missing from
    ///     the catalog are dropped; the textarea remains canonical.
    /// </summary>
    private List<FacetPickerRowViewModel> BuildPickerRows(Predicate predicate)
    {
        var rows = new List<FacetPickerRowViewModel>();
        if (_facetCatalog is null) return rows;

        switch (predicate)
        {
            case Predicate.Term term:
                AppendTerm(term, rows);
                break;

            case Predicate.And and:
                foreach (var child in and.Children)
                {
                    if (child is Predicate.Term t) AppendTerm(t, rows);
                    else
                    {
                        // Any non-Term child means the AST is structurally
                        // richer than a flat AND-of-Terms; bail out so the
                        // operator works exclusively from the raw textarea.
                        rows.Clear();
                        return rows;
                    }
                }
                break;
        }

        return rows;
    }

    private void AppendTerm(Predicate.Term term, List<FacetPickerRowViewModel> rows)
    {
        // Skip terms whose facet isn't in the curated picker catalog -- the
        // raw textarea still carries them; the picker just won't re-hydrate
        // a row it has no input control for.
        if (_facetCatalog?.FindByFacet(term.Facet) is null) return;

        rows.Add(new FacetPickerRowViewModel(
            Facet: term.Facet,
            Op: OpKey(term.Op),
            Value: TermValueText(term.Value, term.Op)));
    }

    private static string OpKey(PredicateOp op) => op switch
    {
        PredicateOp.Eq => "eq",
        PredicateOp.Neq => "neq",
        PredicateOp.Gte => "gte",
        PredicateOp.Gt => "gt",
        PredicateOp.Lte => "lte",
        PredicateOp.Lt => "lt",
        PredicateOp.In => "in",
        PredicateOp.NotIn => "not_in",
        PredicateOp.Between => "between",
        PredicateOp.Matches => "matches",
        PredicateOp.Contains => "contains",
        PredicateOp.AnyIn => "any_in",
        PredicateOp.AllIn => "all_in",
        PredicateOp.InCidr => "in_cidr",
        _ => op.ToString().ToLowerInvariant()
    };

    private static string TermValueText(object value, PredicateOp op)
    {
        if (op == PredicateOp.Between && value is string[] bounds && bounds.Length == 2)
            return string.Concat(bounds[0], ",", bounds[1]);

        return value switch
        {
            string[] arr => string.Join(",", arr),
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            null => string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }
}