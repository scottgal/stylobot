using System.Globalization;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;

// Same alias dance as PolicyStackPresenter / SqlitePolicyDecisionLog: the
// "PolicyAction" name is ambiguous (legacy enum vs Policies.Rules record).
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Surfaces candidate override conflicts inside an effective rule
///     list. This is intentionally a SYNTACTIC pass -- the explainer
///     panel (B5) will perform real semantic evaluation against a sample
///     request. The Stack tab uses these annotations to nudge the
///     operator toward investigation, not to claim with certainty that
///     a given override fires today.
///
///     <para>
///     Algorithm (walk the most-specific-first effective list):
///     </para>
///     <list type="number">
///       <item><description>
///         For each rule R, compare against every ancestor-scope rule A
///         (A's scope is strictly less specific than R's).
///       </description></item>
///       <item><description>
///         If R and A have different action verbs and their predicates
///         <em>might</em> overlap, emit a conflict where R "overrides" A.
///       </description></item>
///       <item><description>
///         "Might overlap" is a deliberately cheap heuristic -- we are
///         NOT running a SAT solver. The cases we surface today catch the
///         common authoring slips: a downstream block that shadows an
///         upstream allow-baseline, or a broad rule that doesn't restrict
///         a facet the ancestor already filtered.
///       </description></item>
///     </list>
/// </summary>
public class PolicyConflictAnalyzer
{
    /// <summary>
    ///     Analyse an effective rule list (resolver order: most-specific
    ///     first) and emit conflict annotations. Virtual so tests can
    ///     decorate with a counting wrapper without retro-fitting an
    ///     interface; production registers the concrete class directly.
    /// </summary>
    public virtual IReadOnlyList<PolicyConflictViewModel> Analyse(
        IReadOnlyList<EffectiveRule> effectiveRules)
    {
        if (effectiveRules.Count < 2) return Array.Empty<PolicyConflictViewModel>();

        var conflicts = new List<PolicyConflictViewModel>();

        for (var i = 0; i < effectiveRules.Count; i++)
        {
            var owner = effectiveRules[i];
            var ownerSpec = owner.SourceScope.Specificity;
            for (var j = 0; j < effectiveRules.Count; j++)
            {
                if (i == j) continue;
                var ancestor = effectiveRules[j];
                if (ancestor.SourceScope.Specificity >= ownerSpec) continue;

                if (SameActionKind(owner.Rule.Action, ancestor.Rule.Action)) continue;
                if (!PredicatesMightOverlap(owner.Rule.Predicate, ancestor.Rule.Predicate)) continue;

                conflicts.Add(BuildConflict(owner, ancestor));
            }
        }

        return conflicts;
    }

    // -------- Heuristics --------

    private static bool SameActionKind(RuleAction a, RuleAction b) => (a, b) switch
    {
        (RuleAction.Allow, RuleAction.Allow) => true,
        (RuleAction.Block, RuleAction.Block) => true,
        (RuleAction.Observe, RuleAction.Observe) => true,
        (RuleAction.Tag, RuleAction.Tag) => true,
        (RuleAction.Challenge, RuleAction.Challenge) => true,
        (RuleAction.RateLimit, RuleAction.RateLimit) => true,
        _ => false
    };

    /// <summary>
    ///     Syntactic "might overlap" check. We over-report when in doubt --
    ///     the operator can dismiss false positives via the explainer, but
    ///     a quietly-missed override is the kind of thing that gets
    ///     people paged.
    /// </summary>
    private static bool PredicatesMightOverlap(Predicate ownerPredicate, Predicate ancestorPredicate)
    {
        var ownerFacets = CollectFacets(ownerPredicate);
        var ancestorFacets = CollectFacets(ancestorPredicate);

        // Case 1: ancestor's predicate is an "Allow-style baseline" -- e.g.
        // `is_human = true` or any unconditional human-allow. Downstream
        // rules that reference bot facets clearly carve a slice OUT of the
        // baseline.
        if (LooksLikeBaselineAllowOnHumanity(ancestorPredicate)
            && OwnerReferencesBotSignals(ownerPredicate))
            return true;

        // Case 2: ancestor restricts a facet (e.g. `geo.country in (CN, RU)`)
        // and owner does NOT mention that facet at all -- owner is broader
        // and could match the slice the ancestor already covers.
        var ancestorOnlyFacets = ancestorFacets.Except(ownerFacets, StringComparer.Ordinal).ToArray();
        var ownerOnlyFacets = ownerFacets.Except(ancestorFacets, StringComparer.Ordinal).ToArray();
        var sharedFacets = ancestorFacets.Intersect(ownerFacets, StringComparer.Ordinal).ToArray();

        // If they share at least one facet, they're talking about the same
        // axis -- conservatively assume they might overlap.
        if (sharedFacets.Length > 0) return true;

        // If neither predicate's facets overlap at all AND each filters a
        // disjoint slice the other doesn't constrain, they govern different
        // axes -- skip.
        if (ancestorOnlyFacets.Length > 0 && ownerOnlyFacets.Length > 0) return false;

        // Owner has no facets the ancestor doesn't, ancestor has none owner doesn't,
        // and they share none either -- empty predicate territory; conservatively
        // report.
        return true;
    }

    private static bool LooksLikeBaselineAllowOnHumanity(Predicate predicate)
    {
        // Pure baseline: a single `is_human = true` (or `= false`) term. The
        // pattern that matters in practice is `is_human = true` paired with
        // an Allow action -- the "default allow humans" rule lives at the
        // domain level.
        if (predicate is Predicate.Term term)
        {
            return string.Equals(term.Facet, "is_human", StringComparison.Ordinal)
                   && term.Op == PredicateOp.Eq;
        }
        return false;
    }

    private static bool OwnerReferencesBotSignals(Predicate ownerPredicate)
    {
        // A downstream rule references a bot signal if any term mentions
        // `bot.*` or `score.bot_probability` -- the two facets the seed
        // YAML uses to express "this is a scraper / crawler / suspicious".
        var facets = CollectFacets(ownerPredicate);
        foreach (var f in facets)
        {
            if (f.StartsWith("bot.", StringComparison.Ordinal)) return true;
            if (string.Equals(f, "score.bot_probability", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IReadOnlyCollection<string> CollectFacets(Predicate predicate)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        Walk(predicate, set);
        return set;
    }

    private static void Walk(Predicate node, HashSet<string> facets)
    {
        switch (node)
        {
            case Predicate.And and:
                foreach (var child in and.Children) Walk(child, facets);
                break;
            case Predicate.Or or:
                foreach (var child in or.Children) Walk(child, facets);
                break;
            case Predicate.Term term:
                facets.Add(term.Facet);
                break;
        }
    }

    // -------- Render --------

    private static PolicyConflictViewModel BuildConflict(EffectiveRule owner, EffectiveRule ancestor)
    {
        var severity = SeverityFor(owner.Rule.Action, ancestor.Rule.Action);
        var ancestorPill = SourcePillFor(ancestor.SourceScope);
        var ownerLabel = ScopeLabelFor(owner.SourceScope);
        var ancestorLabel = ScopeLabelFor(ancestor.SourceScope);
        var ownerPredicateText = PredicateFormatter.Format(owner.Rule.Predicate);
        var ancestorPredicateText = PredicateFormatter.Format(ancestor.Rule.Predicate);
        var ownerAction = ActionShortName(owner.Rule.Action);
        var ancestorAction = ActionShortName(ancestor.Rule.Action);
        var predicateSummary = PredicateSummary(owner.Rule.Predicate);
        var ancestorShort = AncestorShortLabel(ancestor.Rule.Predicate, ancestor.Rule.Action);

        var headline = $"overrides {ancestorPill} {ancestorShort} for {predicateSummary}";
        var detail =
            $"At {ownerLabel} a rule {ownerPredicateText} → {ownerAction} fires before the " +
            $"{ancestorLabel} rule {ancestorPredicateText} → {ancestorAction}. When the " +
            $"{ownerLabel.ToLowerInvariant().Split(' ')[0]}-level predicate matches, the ancestor's " +
            "verdict is shadowed.";

        return new PolicyConflictViewModel(
            OwnerRuleId: owner.Rule.Id,
            OverriddenRuleId: ancestor.Rule.Id,
            OwnerScope: owner.SourceScope,
            OverriddenScope: ancestor.SourceScope,
            Severity: severity,
            Headline: headline,
            Detail: detail);
    }

    private static string SeverityFor(RuleAction owner, RuleAction ancestor) => (owner, ancestor) switch
    {
        // Hard conflicts: Block-over-Allow or Allow-over-Block. The operator
        // almost certainly cares.
        (RuleAction.Block, RuleAction.Allow) => "warning",
        (RuleAction.Allow, RuleAction.Block) => "warning",
        // Softer overrides: Tag / Observe on top of Allow are informational
        // -- the verdict still ends Allow but extra signal carries through.
        (RuleAction.Tag, RuleAction.Allow) => "info",
        (RuleAction.Observe, RuleAction.Allow) => "info",
        _ => "info"
    };

    private static string SourcePillFor(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain => "DOMAIN",
        PolicyScope.Subdomain => "SUBDOMAIN",
        PolicyScope.Endpoint => "ENDPOINT",
        _ => "GLOBAL"
    };

    private static string ScopeLabelFor(PolicyScope scope) => scope switch
    {
        PolicyScope.Wildcard => "GLOBAL",
        PolicyScope.Domain d => $"DOMAIN {d.DomainName}",
        PolicyScope.Subdomain s => $"SUBDOMAIN {s.SubdomainName}",
        PolicyScope.Endpoint e => $"ENDPOINT {e.PathTemplate}",
        _ => "GLOBAL"
    };

    private static string ActionShortName(RuleAction action) => action switch
    {
        RuleAction.Allow => "Allow",
        RuleAction.Block => "Block",
        RuleAction.Observe => "Observe",
        RuleAction.Tag tag => $"Tag '{tag.Name}'",
        RuleAction.Challenge c => $"Challenge ({c.Kind})",
        RuleAction.RateLimit r =>
            $"Rate-limit {r.RequestsPerMinute.ToString(CultureInfo.InvariantCulture)}/min",
        _ => action.GetType().Name
    };

    // Short label used inside the headline for the ancestor side. For the
    // canonical `is_human = true → Allow` baseline we collapse to
    // "allow-is-human" because that's the pattern operators recognise.
    private static string AncestorShortLabel(Predicate ancestorPredicate, RuleAction ancestorAction)
    {
        if (LooksLikeBaselineAllowOnHumanity(ancestorPredicate) && ancestorAction is RuleAction.Allow)
            return "allow-is-human";
        return ActionShortName(ancestorAction).ToLowerInvariant();
    }

    // Headline tail -- summarises the owner predicate as a short
    // human-readable phrase. For the seed example
    // `bot.type in (scraper, crawler) and score.bot_probability >= 0.7`
    // this renders "scraper / crawler matches when score.bot_probability >= 0.7".
    private static string PredicateSummary(Predicate predicate)
    {
        // Walk one level: if it's an AND, summarise each child and join.
        if (predicate is Predicate.And and && and.Children.Length > 0)
        {
            var parts = new List<string>(and.Children.Length);
            foreach (var child in and.Children)
                parts.Add(TermSummary(child));
            return string.Join(" when ", parts);
        }
        return TermSummary(predicate);
    }

    private static string TermSummary(Predicate node) => node switch
    {
        Predicate.Term { Facet: "bot.type", Op: PredicateOp.In, Value: string[] arr } =>
            string.Join(" / ", arr) + " matches",
        Predicate.Term term => PredicateFormatter.Format(term),
        _ => PredicateFormatter.Format(node)
    };
}
