using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.Policies.Telemetry;
using Mostlylucid.BotDetection.UI.Models;

// The "PolicyAction" name is ambiguous inside the Mostlylucid.BotDetection.Policies tree
// (legacy enum at the parent namespace shadows the new record at .Rules). Same alias dance
// used in SqlitePolicyDecisionLog / PolicyEffectivenessCache so the presenter does not
// silently bind to the legacy enum when we pattern-match on Allow / Block / etc.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the <see cref="PolicyStackViewModel"/> the
///     <c>SbPolicyStack</c> view component renders. The presenter is the
///     only place that talks to <see cref="IPolicyResolver"/>,
///     <see cref="IPolicyEffectivenessCache"/>, and
///     <see cref="ISignalCatalog"/> -- the Razor partials stay declarative
///     and DI-free.
/// </summary>
public sealed class PolicyStackPresenter
{
    private readonly IPolicyResolver _resolver;
    private readonly IPolicyEffectivenessCache _effectiveness;
    private readonly ISignalCatalog _signalCatalog;

    public PolicyStackPresenter(
        IPolicyResolver resolver,
        IPolicyEffectivenessCache effectiveness,
        ISignalCatalog signalCatalog)
    {
        _resolver = resolver;
        _effectiveness = effectiveness;
        _signalCatalog = signalCatalog;
    }

    /// <summary>
    ///     Build the view model. The shape of the call is the same across
    ///     embeds; the divergence is internal -- <see cref="PolicyStackEmbed.StatusBadge"/>
    ///     skips the per-rule aggregate fan-out because the badge only needs
    ///     the rule count and the last-edit timestamp.
    /// </summary>
    public async Task<PolicyStackViewModel> BuildAsync(
        PolicyScope scope,
        PolicyStackEmbed embed,
        string activeTab,
        TimeSpan aggregateWindow,
        bool canEdit,
        CancellationToken ct = default)
    {
        var effective = await _resolver.EffectiveAsync(scope, ct).ConfigureAwait(false);
        var breadcrumb = BuildBreadcrumb(scope);
        var scopeHash = ComputeScopeHash(scope);

        // StatusBadge intentionally does NOT call GetManyAsync; the badge only
        // needs the rule count and the last-edit timestamp, so a fan-out across
        // every effective rule would be wasted work on a hot panel that may
        // render in dozens of dashboard cards at once.
        if (embed == PolicyStackEmbed.StatusBadge)
        {
            DateTimeOffset? latest = effective.Count == 0
                ? null
                : effective.Max(static e => e.Rule.CreatedAt);
            return new PolicyStackViewModel(
                Scope: scope,
                BreadcrumbPath: breadcrumb,
                Embed: embed,
                ActiveTab: activeTab,
                AggregateWindow: aggregateWindow,
                CanEdit: canEdit,
                Rows: Array.Empty<PolicyStackRowViewModel>(),
                TotalEffectiveRules: effective.Count,
                RulesTriggeredInWindow: 0,
                LatestEditAt: latest,
                ScopeHash: scopeHash);
        }

        // Bulk read all aggregates in a single hop. The cache serves these from
        // the per-rule ring buffer in well under 100us for hot rules; cold rules
        // fall through to the durable log.
        IReadOnlyDictionary<Guid, PolicyDecisionAggregate> aggregates;
        if (effective.Count == 0)
        {
            aggregates = new Dictionary<Guid, PolicyDecisionAggregate>();
        }
        else
        {
            var ruleIds = new Guid[effective.Count];
            for (var i = 0; i < effective.Count; i++) ruleIds[i] = effective[i].Rule.Id;
            aggregates = await _effectiveness
                .GetManyAsync(ruleIds, aggregateWindow, ct)
                .ConfigureAwait(false);
        }

        var rows = new List<PolicyStackRowViewModel>(effective.Count);
        var triggeredInWindow = 0;
        DateTimeOffset? latestEdit = null;
        foreach (var entry in effective)
        {
            aggregates.TryGetValue(entry.Rule.Id, out var aggregate);
            var row = BuildRow(entry, aggregate, aggregateWindow);
            rows.Add(row);
            if (row.TriggerCount > 0) triggeredInWindow++;
            if (latestEdit is null || entry.Rule.CreatedAt > latestEdit) latestEdit = entry.Rule.CreatedAt;
        }

        return new PolicyStackViewModel(
            Scope: scope,
            BreadcrumbPath: breadcrumb,
            Embed: embed,
            ActiveTab: activeTab,
            AggregateWindow: aggregateWindow,
            CanEdit: canEdit,
            Rows: rows,
            TotalEffectiveRules: rows.Count,
            RulesTriggeredInWindow: triggeredInWindow,
            LatestEditAt: latestEdit,
            ScopeHash: scopeHash);
    }

    // -------- Breadcrumb walk --------

    private static IReadOnlyList<PolicyScope> BuildBreadcrumb(PolicyScope scope)
    {
        // Wildcard first, then narrow. The opposite order to the resolver's
        // scope-walk -- the resolver wants most-specific first to short-circuit
        // matching; the breadcrumb is read left-to-right by the operator.
        return scope switch
        {
            PolicyScope.Endpoint e => new PolicyScope[]
            {
                new PolicyScope.Wildcard(),
                new PolicyScope.Domain(e.DomainName),
                new PolicyScope.Subdomain(e.DomainName, e.SubdomainName),
                e
            },
            PolicyScope.Subdomain s => new PolicyScope[]
            {
                new PolicyScope.Wildcard(),
                new PolicyScope.Domain(s.DomainName),
                s
            },
            PolicyScope.Domain d => new PolicyScope[]
            {
                new PolicyScope.Wildcard(),
                d
            },
            _ => new PolicyScope[] { new PolicyScope.Wildcard() }
        };
    }

    // -------- Row construction --------

    private PolicyStackRowViewModel BuildRow(
        EffectiveRule entry,
        PolicyDecisionAggregate? aggregate,
        TimeSpan window)
    {
        var (verdict, color) = RenderAction(entry.Rule.Action);
        var sourcePill = SourcePillFor(entry.SourceScope);
        var modePill = entry.Rule.Mode.ToString().ToUpperInvariant();
        var predicateText = PredicateFormatter.Format(entry.Rule.Predicate);
        var chips = BuildChips(entry.Rule.Predicate);

        var matched = aggregate?.Matched ?? 0;
        var total = aggregate?.TotalEvaluations ?? 0;
        IReadOnlyDictionary<string, int> winDist =
            aggregate?.WinDistribution ?? new Dictionary<string, int>();
        var distributionLine = RenderDistributionLine(winDist, matched);

        var p50 = aggregate?.P50LatencyMicros ?? 0L;
        var p99 = aggregate?.P99LatencyMicros ?? 0L;
        var latencyLine = (p50 > 0 || p99 > 0) ? RenderLatencyLine(p50, p99) : string.Empty;

        var metadataLine = RenderMetadataLine(entry.Rule);
        var isObserve = entry.Rule.Mode == PolicyMode.Observe;
        var hasNoHits = matched == 0 && window >= TimeSpan.FromDays(7);

        return new PolicyStackRowViewModel(
            RuleId: entry.Rule.Id,
            SourceScope: entry.SourceScope,
            IsInherited: entry.IsInherited,
            SourcePill: sourcePill,
            Mode: entry.Rule.Mode,
            ModePill: modePill,
            ActionVerdict: verdict,
            ActionColorClass: color,
            PredicateText: predicateText,
            Chips: chips,
            TriggerCount: matched,
            TotalEvaluations: total,
            WinDistribution: winDist,
            DistributionLine: distributionLine,
            P50LatencyMicros: p50,
            P99LatencyMicros: p99,
            LatencyLine: latencyLine,
            CreatedAt: entry.Rule.CreatedAt,
            MetadataLine: metadataLine,
            IsObserveMode: isObserve,
            HasNoHits: hasNoHits);
    }

    private static string SourcePillFor(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain => "DOMAIN",
        PolicyScope.Subdomain => "SUBDOMAIN",
        PolicyScope.Endpoint => "ENDPOINT",
        _ => "GLOBAL"
    };

    // Canonical dashboard verdict palette (feedback_dashboard_color_semantics):
    // error  = danger/bot      (Block)
    // warning = caution        (Challenge / RateLimit)
    // success = good/human     (Allow)
    // info    = uncertain/neutral (Tag / Observe)
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

    // -------- Predicate -> chips --------
    //
    // The chip list is a *flattened* view of the predicate tree. AND-children
    // emit one chip each in order; OR-children emit a structural OR marker
    // between them. Nested AND/OR groups are bracketed by structural "(" / ")"
    // chips so the operator can still read the grouping.

    private IReadOnlyList<PolicyStackChipViewModel> BuildChips(Predicate predicate)
    {
        var chips = new List<PolicyStackChipViewModel>();
        FlattenInto(predicate, chips, isInsideAnd: true);
        return chips;
    }

    private void FlattenInto(Predicate node, List<PolicyStackChipViewModel> chips, bool isInsideAnd)
    {
        switch (node)
        {
            case Predicate.And and:
                for (var i = 0; i < and.Children.Length; i++)
                {
                    if (i > 0 && !isInsideAnd) chips.Add(StructuralChip("AND"));
                    FlattenInto(and.Children[i], chips, isInsideAnd: true);
                }
                break;

            case Predicate.Or or:
                // OR inside a flat AND list needs visible grouping or it
                // looks like the OR'd terms are AND'd siblings.
                var addParens = chips.Count > 0;
                if (addParens) chips.Add(StructuralChip("("));
                for (var i = 0; i < or.Children.Length; i++)
                {
                    if (i > 0) chips.Add(StructuralChip("OR"));
                    FlattenInto(or.Children[i], chips, isInsideAnd: false);
                }
                if (addParens) chips.Add(StructuralChip(")"));
                break;

            case Predicate.Term term:
                chips.Add(BuildTermChip(term));
                break;
        }
    }

    private static PolicyStackChipViewModel StructuralChip(string keyword) =>
        new(keyword, string.Empty, string.Empty, string.Empty, IsStructural: true);

    private PolicyStackChipViewModel BuildTermChip(Predicate.Term term)
    {
        var op = OpText(term.Op);
        var valueText = ValueText(term.Value, term.Op);
        var tooltip = _signalCatalog.TryGet(term.Facet)?.Short ?? string.Empty;
        return new PolicyStackChipViewModel(term.Facet, op, valueText, tooltip, IsStructural: false);
    }

    // Mirrors PredicateFormatter.OpText -- intentionally duplicated here so a
    // future tweak to the chip vocabulary (e.g. "matches" -> "~") doesn't drag
    // the round-trippable text form along with it.
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

    private static string ValueText(object value, PredicateOp op)
    {
        if (op == PredicateOp.Between && value is string[] bounds && bounds.Length == 2)
            return $"{bounds[0]} and {bounds[1]}";

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
            null => "null",
            _ => value.ToString() ?? string.Empty
        };
    }

    // -------- Aggregate signal lines --------

    private static string RenderDistributionLine(
        IReadOnlyDictionary<string, int> winDistribution,
        int matched)
    {
        if (matched == 0 || winDistribution.Count == 0) return string.Empty;

        // Sum to derive % share. We use `matched` as the denominator so the
        // percentages add to 100 even when the aggregate's WinDistribution
        // covers fewer rows (e.g. an Observe rule that matched but never won).
        var denom = winDistribution.Values.Sum();
        if (denom == 0) denom = matched;
        if (denom == 0) return string.Empty;

        var parts = new List<string>(winDistribution.Count);
        foreach (var (kind, count) in winDistribution.OrderByDescending(kv => kv.Value))
        {
            var pct = (int)Math.Round(100.0 * count / denom, MidpointRounding.AwayFromZero);
            parts.Add($"{pct}% {kind} ({count})");
        }
        return string.Join(" · ", parts);
    }

    private static string RenderLatencyLine(long p50Micros, long p99Micros)
    {
        var p50Ms = (p50Micros / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        var p99Ms = (p99Micros / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
        return $"p50 {p50Ms}ms · p99 {p99Ms}ms";
    }

    private static string RenderMetadataLine(PolicyRule rule)
    {
        if (rule.Source.StartsWith("embedded:", StringComparison.Ordinal))
            return "default";

        var ageDays = (DateTimeOffset.UtcNow - rule.CreatedAt).TotalDays;
        return ageDays switch
        {
            < 1 => "edited today",
            < 2 => "edited 1d ago",
            _ => $"edited {(int)ageDays}d ago"
        };
    }

    // -------- Scope hash --------
    //
    // SHA-256 of a canonical "kind|domain|subdomain|template" string. The
    // first 16 hex characters give the room for B6's SignalR group names
    // (collision risk on 16 hex = 64 bits is irrelevant for an in-process
    // group dictionary).

    internal static string ComputeScopeHash(PolicyScope scope)
    {
        var canonical = scope switch
        {
            PolicyScope.Wildcard => "wildcard||||",
            PolicyScope.Domain d => $"domain|{d.DomainName}|||",
            PolicyScope.Subdomain s => $"subdomain|{s.DomainName}|{s.SubdomainName}||",
            PolicyScope.Endpoint e => $"endpoint|{e.DomainName}|{e.SubdomainName}|{e.PathTemplate}|",
            _ => "unknown||||"
        };
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        // First 8 bytes -> 16 hex chars.
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
