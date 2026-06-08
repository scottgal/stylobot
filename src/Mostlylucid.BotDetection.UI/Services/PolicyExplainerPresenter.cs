using System.Globalization;
using System.Text;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.UI.Models;

// Same alias dance as PolicyStackPresenter -- the legacy enum at the parent
// namespace shadows the new record at .Rules; using the alias keeps every
// `Allow / Block / etc.` pattern-match unambiguous.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the <see cref="PolicyExplainerViewModel"/> the SbPolicyStack
///     explainer panel renders. Given an effective rule list + a fingerprint,
///     pulls the most recent decision cycle from the log and replays each
///     rule's predicate through <see cref="PredicateTraceEvaluator"/> so the
///     panel can show the per-term breakdown without a re-evaluation cost on
///     the request path.
/// </summary>
public sealed class PolicyExplainerPresenter
{
    private const int DecisionRowCap = 200;

    /// <summary>
    ///     The decision-cycle clustering window. Two adjacent rows belong to
    ///     the same cycle when their observation timestamps differ by less
    ///     than this -- the evaluator emits every rule in one tight burst, so
    ///     a few tens of milliseconds is plenty of slack.
    /// </summary>
    private static readonly TimeSpan CycleSlack = TimeSpan.FromMilliseconds(50);

    private readonly IPolicyResolver _resolver;
    private readonly IPolicyDecisionLog _decisionLog;
    private readonly ISignalCatalog _signalCatalog;

    public PolicyExplainerPresenter(
        IPolicyResolver resolver,
        IPolicyDecisionLog decisionLog,
        ISignalCatalog signalCatalog)
    {
        _resolver = resolver;
        _decisionLog = decisionLog;
        _signalCatalog = signalCatalog;
    }

    /// <summary>
    ///     Build the explainer view model for the given scope + fingerprint.
    ///     An empty fingerprint returns a placeholder shape so the picker can
    ///     still render. An unknown fingerprint returns the same placeholder
    ///     plus an empty rule list -- callers distinguish via <c>Rules.Count</c>.
    /// </summary>
    public async Task<PolicyExplainerViewModel> BuildAsync(
        PolicyScope scope,
        string fingerprint,
        bool locked,
        CancellationToken ct = default)
    {
        fingerprint ??= string.Empty;

        if (fingerprint.Length == 0)
        {
            // Picker shown but no fingerprint entered yet -- return a fully
            // formed empty view model so the partial renders the placeholder
            // copy without branching on null.
            return new PolicyExplainerViewModel(
                FingerprintId: string.Empty,
                ReplayedAt: null,
                Rules: Array.Empty<PolicyExplainerRuleViewModel>(),
                WinnerRuleId: null,
                ContextSummary: string.Empty,
                Locked: locked);
        }

        var effective = await _resolver.EffectiveAsync(scope, ct).ConfigureAwait(false);
        var rows = await _decisionLog
            .GetByFingerprintAsync(fingerprint, max: DecisionRowCap, ct)
            .ConfigureAwait(false);

        if (rows.Count == 0 || effective.Count == 0)
        {
            return new PolicyExplainerViewModel(
                FingerprintId: fingerprint,
                ReplayedAt: null,
                Rules: Array.Empty<PolicyExplainerRuleViewModel>(),
                WinnerRuleId: null,
                ContextSummary: string.Empty,
                Locked: locked);
        }

        var cycle = SelectMostRecentCycle(rows);
        var cycleByRule = new Dictionary<Guid, PolicyDecision>(cycle.Count);
        Guid winnerRuleId = cycle[0].WinnerRuleId;
        DateTimeOffset replayedAt = cycle[0].ObservedAt;
        foreach (var row in cycle)
        {
            cycleByRule[row.RuleId] = row;
            if (row.ObservedAt > replayedAt) replayedAt = row.ObservedAt;
        }

        // The signals dictionary the evaluator originally ran with isn't on
        // the decision row today (A7 hasn't landed that field yet). Build a
        // best-effort signals dictionary from the rules that ACTUALLY matched
        // in the cycle: every term those rules carried is, by definition, a
        // signal whose value matched the term's authored value. This is enough
        // to keep the trace coherent for the operator -- the explainer's
        // future enrichment is when log rows carry richer context (see plan).
        var signals = SynthesiseSignals(effective, cycleByRule);

        var ruleVms = new List<PolicyExplainerRuleViewModel>(effective.Count);
        foreach (var entry in effective)
        {
            ruleVms.Add(BuildRuleRow(entry, cycleByRule, winnerRuleId, signals));
        }

        var contextSummary = BuildContextSummary(fingerprint, winnerRuleId, effective);

        return new PolicyExplainerViewModel(
            FingerprintId: fingerprint,
            ReplayedAt: replayedAt,
            Rules: ruleVms,
            WinnerRuleId: winnerRuleId,
            ContextSummary: contextSummary,
            Locked: locked);
    }

    // -------- Cycle selection --------
    //
    // The log returns every row for this fingerprint sorted ObservedAt
    // ascending. We want the MOST RECENT contiguous block of rows that all
    // share the same WinnerRuleId AND fall inside CycleSlack of each other.
    // Walk backwards: pop the last row, then accept earlier rows as long as
    // they're close-in-time and carry the same winner.

    private static IReadOnlyList<PolicyDecision> SelectMostRecentCycle(IReadOnlyList<PolicyDecision> rows)
    {
        if (rows.Count == 0) return Array.Empty<PolicyDecision>();

        var last = rows[^1];
        var winner = last.WinnerRuleId;
        var observedAt = last.ObservedAt;

        var cycle = new List<PolicyDecision> { last };
        for (var i = rows.Count - 2; i >= 0; i--)
        {
            var row = rows[i];
            if (row.WinnerRuleId != winner) break;
            if (observedAt - row.ObservedAt > CycleSlack) break;
            cycle.Add(row);
        }

        // We walked backwards -- restore the natural ascending order so the
        // caller can iterate per-rule in the same order the evaluator ran.
        cycle.Reverse();
        return cycle;
    }

    // -------- Signals synthesis --------
    //
    // For every rule in the effective list whose cycle row was Matched=true,
    // every Term that the rule's predicate carries was, at the moment the
    // evaluator ran, satisfied. That gives us a (facet -> authored-value)
    // pair we can stash in the synthesised signals dictionary. Once richer
    // context lands on PolicyDecision (future enrichment) this becomes the
    // real recorded snapshot; today it's the best fidelity available without
    // changing the decision-log schema.

    private static IReadOnlyDictionary<string, object?> SynthesiseSignals(
        IReadOnlyList<EffectiveRule> effective,
        IReadOnlyDictionary<Guid, PolicyDecision> cycleByRule)
    {
        var signals = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in effective)
        {
            if (!cycleByRule.TryGetValue(entry.Rule.Id, out var row)) continue;
            if (!row.Matched) continue;
            CollectFacets(entry.Rule.Predicate, signals);
        }
        return signals;
    }

    private static void CollectFacets(Predicate predicate, Dictionary<string, object?> signals)
    {
        switch (predicate)
        {
            case Predicate.And and:
                foreach (var child in and.Children) CollectFacets(child, signals);
                break;
            case Predicate.Or or:
                foreach (var child in or.Children) CollectFacets(child, signals);
                break;
            case Predicate.Term term:
                if (signals.ContainsKey(term.Facet)) break;
                signals[term.Facet] = ProjectAuthoredValue(term);
                break;
        }
    }

    // For set ops the authored value is a string[]; the trace evaluator's
    // InList compares the facet's string projection against that list, so we
    // project the first list entry as the synthesised facet value -- the
    // term that wrote the dictionary entry will read it back and match.
    private static object? ProjectAuthoredValue(Predicate.Term term) => term.Op switch
    {
        PredicateOp.In or PredicateOp.NotIn or PredicateOp.AnyIn or PredicateOp.AllIn
            => term.Value is string[] arr && arr.Length > 0 ? arr[0] : term.Value,
        PredicateOp.Between => term.Value is string[] bounds && bounds.Length == 2 ? (object)bounds[0] : term.Value,
        _ => term.Value
    };

    // -------- Per-rule row --------

    private PolicyExplainerRuleViewModel BuildRuleRow(
        EffectiveRule entry,
        IReadOnlyDictionary<Guid, PolicyDecision> cycleByRule,
        Guid winnerRuleId,
        IReadOnlyDictionary<string, object?> signals)
    {
        var (verdict, color) = RenderAction(entry.Rule.Action);
        var sourcePill = SourcePillFor(entry.SourceScope);
        var predicateText = PredicateFormatter.Format(entry.Rule.Predicate);

        var hasRow = cycleByRule.TryGetValue(entry.Rule.Id, out var row);
        var wasSkipped = hasRow && row!.EvalLatencyTicks == 0 && !row.Matched;

        if (!hasRow || wasSkipped)
        {
            // A rule the resolver returned but the cycle log didn't touch (or
            // touched only as a zero-latency skipped row) was never evaluated
            // because an earlier rule won. Render the row with an empty trace
            // + skip reason so the operator sees the rule, knows it exists,
            // and knows why it didn't run.
            return new PolicyExplainerRuleViewModel(
                RuleId: entry.Rule.Id,
                SourcePill: sourcePill,
                ActionVerdict: verdict,
                ActionColorClass: color,
                IsWinner: false,
                WasEvaluated: false,
                PredicateText: predicateText,
                Terms: Array.Empty<PolicyExplainerTermViewModel>(),
                TotalEvalMicros: 0,
                SkipReason: "not evaluated -- earlier rule won");
        }

        var trace = PredicateTraceEvaluator.Trace(entry.Rule.Predicate, signals);
        var termVms = new List<PolicyExplainerTermViewModel>(trace.Terms.Count);
        foreach (var outcome in trace.Terms)
        {
            termVms.Add(BuildTermRow(outcome));
        }

        return new PolicyExplainerRuleViewModel(
            RuleId: entry.Rule.Id,
            SourcePill: sourcePill,
            ActionVerdict: verdict,
            ActionColorClass: color,
            IsWinner: entry.Rule.Id == winnerRuleId,
            WasEvaluated: true,
            PredicateText: predicateText,
            Terms: termVms,
            TotalEvalMicros: TicksToMicros(trace.TotalTicks),
            SkipReason: null);
    }

    private PolicyExplainerTermViewModel BuildTermRow(PredicateTermOutcome outcome)
    {
        var description = RenderTermDescription(outcome.Facet, outcome.Op, outcome.Value);
        var tooltip = _signalCatalog.TryGet(outcome.Facet)?.Short ?? string.Empty;
        var indicator = outcome.Matched ? "✓" : "✗";
        var detail = outcome.Matched
            ? $"actual = {RenderValue(outcome.ActualFacetValue)}"
            : outcome.FailureReason ?? string.Empty;
        return new PolicyExplainerTermViewModel(
            Description: description,
            Tooltip: tooltip,
            Matched: outcome.Matched,
            Indicator: indicator,
            Detail: detail,
            EvalMicros: TicksToMicros(outcome.EvalTicks));
    }

    private static long TicksToMicros(long ticks)
    {
        // EvalTicks is TimeSpan ticks (100ns each). 10 ticks per microsecond.
        if (ticks <= 0) return 0;
        return ticks / 10L;
    }

    // -------- Context summary --------

    private static string BuildContextSummary(string fingerprint, Guid winnerRuleId, IReadOnlyList<EffectiveRule> effective)
    {
        // Future enrichment lands richer context (method + path + UA) on the
        // decision log. Until then keep this small but useful: the winner's
        // predicate is the closest thing we have to "what this request looked
        // like" because it's the rule that actually matched.
        var winner = effective.FirstOrDefault(e => e.Rule.Id == winnerRuleId);
        var winnerPredicate = winner is null ? string.Empty : PredicateFormatter.Format(winner.Rule.Predicate);
        return string.IsNullOrEmpty(winnerPredicate)
            ? $"fingerprint {fingerprint}"
            : $"fingerprint {fingerprint}  ·  matched {winnerPredicate}";
    }

    // -------- Predicate -> human-readable text --------

    private static string RenderTermDescription(string facet, PredicateOp op, object value)
    {
        var sb = new StringBuilder();
        sb.Append(facet);
        sb.Append(' ');
        sb.Append(OpText(op));
        sb.Append(' ');
        sb.Append(RenderValue(value));
        return sb.ToString();
    }

    private static string OpText(PredicateOp op) => op switch
    {
        PredicateOp.In => "in",
        PredicateOp.NotIn => "not in",
        PredicateOp.Eq => "=",
        PredicateOp.Neq => "!=",
        PredicateOp.Gte => ">=",
        PredicateOp.Gt => ">",
        PredicateOp.Lte => "<=",
        PredicateOp.Lt => "<",
        PredicateOp.Between => "between",
        PredicateOp.Matches => "matches",
        PredicateOp.Contains => "contains",
        PredicateOp.AnyIn => "any in",
        PredicateOp.AllIn => "all in",
        _ => op.ToString().ToLowerInvariant()
    };

    private static string RenderValue(object? value)
    {
        if (value is null) return "<missing>";
        if (value is string[] arr)
        {
            var sb = new StringBuilder("(");
            for (var i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(arr[i]);
            }
            sb.Append(')');
            return sb.ToString();
        }
        return value switch
        {
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    // -------- Source pill + action verdict (mirrors PolicyStackPresenter) --------

    private static string SourcePillFor(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain => "DOMAIN",
        PolicyScope.Subdomain => "SUBDOMAIN",
        PolicyScope.Endpoint => "ENDPOINT",
        _ => "GLOBAL"
    };

    private static (string Verdict, string Color) RenderAction(RuleAction action) => action switch
    {
        RuleAction.Block => ("Block", "verdict-error"),
        RuleAction.Allow => ("Allow", "verdict-success"),
        RuleAction.Observe => ("Observe", "verdict-info"),
        RuleAction.Tag tag => ($"Tag '{tag.Name}'", "verdict-info"),
        RuleAction.Challenge c => ($"Challenge ({c.Kind})", "verdict-warning"),
        RuleAction.RateLimit r => (
            $"Rate-limit {r.RequestsPerMinute.ToString(CultureInfo.InvariantCulture)}/min",
            "verdict-warning"),
        _ => (action.GetType().Name, "verdict-info")
    };
}
