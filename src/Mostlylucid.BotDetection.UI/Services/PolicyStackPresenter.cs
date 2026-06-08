using System.Globalization;
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
    private readonly PolicyConflictAnalyzer _conflictAnalyzer;
    private readonly PolicyStackFilterOptions _filterOptions;
    private readonly PolicyExplainerPresenter? _explainerPresenter;

    public PolicyStackPresenter(
        IPolicyResolver resolver,
        IPolicyEffectivenessCache effectiveness,
        ISignalCatalog signalCatalog,
        PolicyConflictAnalyzer conflictAnalyzer,
        PolicyStackFilterOptions? filterOptions = null,
        PolicyExplainerPresenter? explainerPresenter = null)
    {
        _resolver = resolver;
        _effectiveness = effectiveness;
        _signalCatalog = signalCatalog;
        _conflictAnalyzer = conflictAnalyzer;
        _filterOptions = filterOptions ?? new PolicyStackFilterOptions();
        _explainerPresenter = explainerPresenter;
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
        PolicyStackFilter? filter = null,
        PolicyStackSort? sort = null,
        string? explainerFingerprint = null,
        bool explainerLocked = false,
        CancellationToken ct = default)
    {
        var effectiveFilter = filter ?? PolicyStackFilter.Empty;
        var effectiveSort = sort ?? PolicyStackSort.Default;

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
                ScopeHash: scopeHash,
                StackGroups: Array.Empty<PolicyStackScopeGroupViewModel>(),
                Conflicts: Array.Empty<PolicyConflictViewModel>(),
                Filter: effectiveFilter,
                Sort: effectiveSort,
                AggregateStrip: null,
                Explainer: null);
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

        // Build EVERY row first, then filter + sort. Building all rows up front
        // keeps row construction idempotent and lets the filter inspect signals
        // that aren't on the rule itself (TriggerCount, p99, distribution).
        var allRows = new List<PolicyStackRowViewModel>(effective.Count);
        // Map row -> EffectiveRule so the Stack-tab grouping can recover the
        // source scope after the filter has reordered things. Cheap because
        // both lists are at most a few dozen entries even on busy scopes.
        var effectiveByRuleId = new Dictionary<Guid, EffectiveRule>(effective.Count);
        DateTimeOffset? latestEdit = null;
        foreach (var entry in effective)
        {
            aggregates.TryGetValue(entry.Rule.Id, out var aggregate);
            var row = BuildRow(entry, aggregate, aggregateWindow);
            allRows.Add(row);
            effectiveByRuleId[entry.Rule.Id] = entry;
            if (latestEdit is null || entry.Rule.CreatedAt > latestEdit) latestEdit = entry.Rule.CreatedAt;
        }

        // Apply filter -> sort -> strip. The order matters: the strip
        // summarises the visible (post-filter) set so the operator sees their
        // narrowed view reflected in the strip too.
        var filteredRows = ApplyFilter(allRows, effectiveFilter);
        var sortedRows = ApplySort(filteredRows, effectiveSort);

        var triggeredInWindow = 0;
        var totalRequests = 0;
        var zeroHits = 0;
        for (var i = 0; i < sortedRows.Count; i++)
        {
            var row = sortedRows[i];
            if (row.TriggerCount > 0) triggeredInWindow++;
            totalRequests += row.TotalEvaluations;
            if (row.TriggerCount == 0) zeroHits++;
        }
        var strip = new PolicyStackAggregateStrip(
            TotalRequestsInWindow: totalRequests,
            RulesVisible: sortedRows.Count,
            RulesTriggeredInWindow: triggeredInWindow,
            RulesWithZeroHits: zeroHits,
            Window: aggregateWindow);

        // Stack-tab fan-out: group + analyse only when the Stack tab is the
        // active surface AND we're rendering the Full embed. The Effective
        // tab path stays cost-free, and EffectiveOnly never shows the Stack
        // tab so the activeTab hint is ignored. Stack grouping consumes the
        // FILTERED rows so the operator's filter narrows what shows inside
        // each scope group too.
        IReadOnlyList<PolicyStackScopeGroupViewModel> stackGroups = Array.Empty<PolicyStackScopeGroupViewModel>();
        IReadOnlyList<PolicyConflictViewModel> conflicts = Array.Empty<PolicyConflictViewModel>();
        if (embed == PolicyStackEmbed.Full
            && string.Equals(activeTab, "stack", StringComparison.OrdinalIgnoreCase))
        {
            conflicts = _conflictAnalyzer.Analyse(effective);
            stackGroups = BuildStackGroups(effective, sortedRows, effectiveByRuleId, conflicts);
        }

        // Build the explainer panel state when the caller supplied a fingerprint
        // (Full embed always; EffectiveOnly only when locked). The presenter
        // is optional in DI so older callers that don't register it keep
        // working -- they just never get an explainer.
        PolicyExplainerViewModel? explainer = null;
        var wantsExplainer = explainerFingerprint is not null
            && (embed == PolicyStackEmbed.Full
                || (embed == PolicyStackEmbed.EffectiveOnly && explainerLocked));
        if (wantsExplainer && _explainerPresenter is not null)
        {
            explainer = await _explainerPresenter
                .BuildAsync(scope, explainerFingerprint ?? string.Empty, explainerLocked, ct)
                .ConfigureAwait(false);
        }

        return new PolicyStackViewModel(
            Scope: scope,
            BreadcrumbPath: breadcrumb,
            Embed: embed,
            ActiveTab: activeTab,
            AggregateWindow: aggregateWindow,
            CanEdit: canEdit,
            Rows: sortedRows,
            TotalEffectiveRules: sortedRows.Count,
            RulesTriggeredInWindow: triggeredInWindow,
            LatestEditAt: latestEdit,
            ScopeHash: scopeHash,
            StackGroups: stackGroups,
            Conflicts: conflicts,
            Filter: effectiveFilter,
            Sort: effectiveSort,
            AggregateStrip: strip,
            Explainer: explainer);
    }

    // -------- Filter --------

    private IReadOnlyList<PolicyStackRowViewModel> ApplyFilter(
        IReadOnlyList<PolicyStackRowViewModel> rows,
        PolicyStackFilter filter)
    {
        if (rows.Count == 0 || !filter.IsActive) return rows;

        var pass = new List<PolicyStackRowViewModel>(rows.Count);
        var now = DateTimeOffset.UtcNow;

        // Pre-compute the @hot cutoff once -- the top-N row by trigger count.
        // Cheap because the row count at a single scope is small (~tens).
        HashSet<Guid>? hotIds = null;
        if (filter.HotOnly)
        {
            hotIds = rows
                .Where(r => r.TriggerCount > 0)
                .OrderByDescending(r => r.TriggerCount)
                .Take(_filterOptions.HotTopN)
                .Select(r => r.RuleId)
                .ToHashSet();
        }

        foreach (var row in rows)
        {
            if (filter.OnlyModified && !row.IsModified) continue;
            if (filter.EditedSince is { } window && now - row.LastEditedAt > window) continue;
            if (!string.IsNullOrEmpty(filter.OnlyScope)
                && !string.Equals(row.ScopeKind, filter.OnlyScope, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrEmpty(filter.OnlyAction)
                && !string.Equals(row.ActionKind, filter.OnlyAction, StringComparison.Ordinal))
                continue;
            if (filter.OnlyObserve && !row.IsObserveMode) continue;
            if (filter.HotOnly && (hotIds is null || !hotIds.Contains(row.RuleId))) continue;
            if (filter.SlowOnly && row.P99LatencyMicros <= _filterOptions.SlowP99Micros) continue;
            if (filter.NoHitsOnly && (row.TriggerCount != 0 || row.TotalEvaluations != 0)) continue;
            if (!string.IsNullOrEmpty(filter.FreeText)
                && row.PredicateText.IndexOf(filter.FreeText, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            pass.Add(row);
        }

        return pass;
    }

    // -------- Sort --------

    private static IReadOnlyList<PolicyStackRowViewModel> ApplySort(
        IReadOnlyList<PolicyStackRowViewModel> rows,
        PolicyStackSort sort)
    {
        // Default sort = resolver order ascending = the order the rows already
        // arrive in. Skip the LINQ overhead on the hot path.
        if (rows.Count <= 1) return rows;
        if (sort.Key == PolicyStackSortKey.Priority && sort.Direction == PolicyStackSortDir.Asc)
            return rows;

        if (sort.Key == PolicyStackSortKey.Priority)
        {
            // Descending priority on the Effective tab: just flip.
            var reversed = new List<PolicyStackRowViewModel>(rows);
            reversed.Reverse();
            return reversed;
        }

        // For Triggers / P99 / PercentBlocked: rows with zero evaluations sort
        // to the END regardless of direction so an empty row never sits at the
        // top of "sort by p99".
        IComparer<PolicyStackRowViewModel> comparer = sort.Key switch
        {
            PolicyStackSortKey.Triggers => Comparer<PolicyStackRowViewModel>.Create(
                (a, b) =>
                {
                    var cmp = a.TriggerCount.CompareTo(b.TriggerCount);
                    return sort.Direction == PolicyStackSortDir.Asc ? cmp : -cmp;
                }),
            PolicyStackSortKey.P99Latency => Comparer<PolicyStackRowViewModel>.Create(
                (a, b) =>
                {
                    if (a.TotalEvaluations == 0 && b.TotalEvaluations == 0) return 0;
                    if (a.TotalEvaluations == 0) return 1;
                    if (b.TotalEvaluations == 0) return -1;
                    var cmp = a.P99LatencyMicros.CompareTo(b.P99LatencyMicros);
                    return sort.Direction == PolicyStackSortDir.Asc ? cmp : -cmp;
                }),
            PolicyStackSortKey.PercentBlocked => Comparer<PolicyStackRowViewModel>.Create(
                (a, b) =>
                {
                    if (a.TotalEvaluations == 0 && b.TotalEvaluations == 0) return 0;
                    if (a.TotalEvaluations == 0) return 1;
                    if (b.TotalEvaluations == 0) return -1;
                    var aPct = PercentBlocked(a);
                    var bPct = PercentBlocked(b);
                    var cmp = aPct.CompareTo(bPct);
                    return sort.Direction == PolicyStackSortDir.Asc ? cmp : -cmp;
                }),
            PolicyStackSortKey.LastEdited => Comparer<PolicyStackRowViewModel>.Create(
                (a, b) =>
                {
                    // Ascending = oldest first, descending = newest first.
                    var cmp = a.LastEditedAt.CompareTo(b.LastEditedAt);
                    return sort.Direction == PolicyStackSortDir.Asc ? cmp : -cmp;
                }),
            _ => null!
        };
        if (comparer is null) return rows;

        var sorted = new List<PolicyStackRowViewModel>(rows);
        sorted.Sort(comparer);
        return sorted;
    }

    private static double PercentBlocked(PolicyStackRowViewModel row)
    {
        if (row.TotalEvaluations == 0) return 0d;
        row.WinDistribution.TryGetValue("block", out var blocks);
        return (double)blocks / row.TotalEvaluations;
    }

    // -------- Stack-tab grouping --------
    //
    // The Effective tab is most-specific-first so the operator sees the
    // winning rule at the top. The Stack tab is ancestor-first so the
    // reader walks the hierarchy left-to-right exactly the way the
    // resolver built it.

    private static IReadOnlyList<PolicyStackScopeGroupViewModel> BuildStackGroups(
        IReadOnlyList<EffectiveRule> effective,
        IReadOnlyList<PolicyStackRowViewModel> filteredRows,
        IReadOnlyDictionary<Guid, EffectiveRule> effectiveByRuleId,
        IReadOnlyList<PolicyConflictViewModel> conflicts)
    {
        // Group the FILTERED rows by their owning scope, preserving the
        // order they arrive in (so the active sort drives the in-group order
        // too). Rows that the filter dropped never appear in a group.
        var groupRows = new Dictionary<PolicyScope, List<PolicyStackRowViewModel>>();
        foreach (var row in filteredRows)
        {
            if (!effectiveByRuleId.TryGetValue(row.RuleId, out var entry)) continue;
            if (!groupRows.TryGetValue(entry.SourceScope, out var bucket))
            {
                bucket = new List<PolicyStackRowViewModel>();
                groupRows[entry.SourceScope] = bucket;
            }
            bucket.Add(row);
        }

        // Attach conflicts to the OWNER scope.
        var groupConflicts = new Dictionary<PolicyScope, List<PolicyConflictViewModel>>();
        foreach (var conflict in conflicts)
        {
            if (!groupConflicts.TryGetValue(conflict.OwnerScope, out var bucket))
            {
                bucket = new List<PolicyConflictViewModel>();
                groupConflicts[conflict.OwnerScope] = bucket;
            }
            bucket.Add(conflict);
        }

        var groups = new List<PolicyStackScopeGroupViewModel>(groupRows.Count);
        foreach (var (scope, scopeRows) in groupRows)
        {
            groupConflicts.TryGetValue(scope, out var scopeConflicts);
            groups.Add(new PolicyStackScopeGroupViewModel(
                Scope: scope,
                ScopeLabel: StackGroupLabel(scope),
                Specificity: scope.Specificity,
                Rows: scopeRows,
                Conflicts: (IReadOnlyList<PolicyConflictViewModel>?)scopeConflicts
                    ?? Array.Empty<PolicyConflictViewModel>()));
        }

        // Ancestor-first ordering. The resolver hands us most-specific-first;
        // OrderBy specificity ascending flips that to wildcard -> endpoint.
        groups.Sort(static (a, b) => a.Specificity.CompareTo(b.Specificity));
        return groups;
    }

    private static string StackGroupLabel(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain d => $"DOMAIN  {d.DomainName}",
        PolicyScope.Subdomain s => $"SUBDOMAIN  {s.SubdomainName}",
        PolicyScope.Endpoint e => $"ENDPOINT  {e.PathTemplate}",
        _ => "GLOBAL"
    };

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
        var isModified = !entry.Rule.Source.StartsWith("embedded:", StringComparison.Ordinal);

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
            HasNoHits: hasNoHits,
            IsModified: isModified,
            // Until A-tier edit telemetry lands LastEditedAt mirrors CreatedAt;
            // it's a separate field so the sort + @since: filter can swap to a
            // dedicated "last touched" column without re-threading rows.
            LastEditedAt: entry.Rule.CreatedAt,
            ActionKind: ActionKind(entry.Rule.Action),
            ScopeKind: ScopeKindToken(entry.SourceScope));
    }

    private static string SourcePillFor(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain => "DOMAIN",
        PolicyScope.Subdomain => "SUBDOMAIN",
        PolicyScope.Endpoint => "ENDPOINT",
        _ => "GLOBAL"
    };

    private static string ScopeKindToken(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "wildcard",
        PolicyScope.Domain => "domain",
        PolicyScope.Subdomain => "subdomain",
        PolicyScope.Endpoint => "endpoint",
        _ => "wildcard"
    };

    private static string ActionKind(RuleAction action) => action switch
    {
        RuleAction.Block => "block",
        RuleAction.Allow => "allow",
        RuleAction.Observe => "observe",
        RuleAction.Tag => "tag",
        RuleAction.Challenge => "challenge",
        RuleAction.RateLimit => "ratelimit",
        _ => action.GetType().Name.ToLowerInvariant()
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
    // Delegates to PolicyScopeKeys.ScopeHash so the broadcaster, the view
    // component, and the middleware all agree on the same canonical pre-image.
    // SHA-256 of a "kind|domain|subdomain|template" string; first 16 hex chars
    // (64 bits) is wide enough for an in-process SignalR group dictionary.

    internal static string ComputeScopeHash(PolicyScope scope)
        => Mostlylucid.BotDetection.UI.Policies.PolicyScopeKeys.ScopeHash(scope);
}
