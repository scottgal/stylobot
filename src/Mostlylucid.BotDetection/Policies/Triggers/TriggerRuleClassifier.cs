using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Triggers;

/// <summary>
///     Decides whether a rule is a "trigger rule" -- one whose predicate references
///     ONLY meter signals (every <see cref="Predicate.Predicate.Term"/>'s
///     <see cref="Predicate.Predicate.Term.Facet"/> starts with <c>meter.</c>).
///     Trigger rules are driven by the oscilloscope MeterTriggerService (in the
///     PrometheusPack) and NOT by the per-request resolver loop. A rule MUST
///     have a non-null <see cref="PolicyRule.Trigger"/> and a meter-only
///     predicate to qualify.
///
///     <para>
///         Mixed predicates (per-request + meter signals) and per-request-only
///         predicates are NEVER trigger rules; they stay on the per-request hot
///         path.
///     </para>
/// </summary>
public static class TriggerRuleClassifier
{
    /// <summary>
    ///     Prefix every meter-signal facet uses. Matches the canonical key shape
    ///     emitted by the PrometheusPack <c>MeterSnapshotSignalContributor</c>.
    /// </summary>
    public const string MeterFacetPrefix = "meter.";

    /// <summary>
    ///     <c>true</c> when <paramref name="rule"/> qualifies as a trigger
    ///     rule. Two paths:
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <b>Phase G backcompat:</b> rule has a non-null
    ///                 <see cref="PolicyRule.Trigger"/> AND every term in the
    ///                 predicate references a <c>meter.*</c> facet. Existing
    ///                 rules authored before T-Expr continue to classify this
    ///                 way -- their <see cref="Rules.RuleTriggerOptions.SustainFor"/>
    ///                 still drives the armed-state machine.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>T-Expr:</b> rule contains a
    ///                 <see cref="Predicate.Predicate.Sustain"/> node anywhere
    ///                 in its predicate whose inner sub-tree is meter-only.
    ///                 The Sustain node's <see cref="Predicate.Predicate.Sustain.Duration"/>
    ///                 replaces <c>SustainFor</c> in the new shape.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </summary>
    public static bool IsTriggerRule(PolicyRule rule)
    {
        if (rule.Trigger is not null && IsMeterOnly(rule.Predicate)) return true;
        return HasSustainOverMeterOnly(rule.Predicate);
    }

    /// <summary>
    ///     Recursive walk: <c>true</c> only when every leaf is a
    ///     <see cref="Predicate.Predicate.Term"/> whose
    ///     <see cref="Predicate.Predicate.Term.Facet"/> starts with
    ///     <see cref="MeterFacetPrefix"/>. An empty And/Or returns
    ///     <c>true</c> (vacuously meter-only) -- consistent with the
    ///     <see cref="PredicateEvaluator"/> identity-element semantics for
    ///     empty composites.
    /// </summary>
    public static bool IsMeterOnly(Predicate.Predicate predicate) =>
        predicate switch
        {
            Predicate.Predicate.And and => and.Children.All(IsMeterOnly),
            Predicate.Predicate.Or or => or.Children.All(IsMeterOnly),
            Predicate.Predicate.Sustain s => IsMeterOnly(s.Inner),
            Predicate.Predicate.Term t => t.Facet.StartsWith(MeterFacetPrefix, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    /// <summary>
    ///     <c>true</c> when the predicate AST contains a
    ///     <see cref="Predicate.Predicate.Sustain"/> node anywhere whose inner
    ///     sub-tree is meter-only -- the T-Expr trigger-rule shape. A composite
    ///     <c>(meter.cpu &gt; 80 FOR 60s) AND bot.type = "Scraper"</c> still
    ///     qualifies because the Sustain's inner references only
    ///     <c>meter.*</c> even though the surrounding And mixes in a
    ///     per-request facet.
    /// </summary>
    public static bool HasSustainOverMeterOnly(Predicate.Predicate predicate) =>
        predicate switch
        {
            Predicate.Predicate.Sustain s => IsMeterOnly(s.Inner),
            Predicate.Predicate.And and => and.Children.Any(HasSustainOverMeterOnly),
            Predicate.Predicate.Or or => or.Children.Any(HasSustainOverMeterOnly),
            _ => false
        };
}
