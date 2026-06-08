namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Per-term outcome of a <see cref="PredicateTraceEvaluator.Trace"/> run.
///     Records the facet that was probed, the operator that was applied, the
///     authored value, the actual facet value the signals dictionary supplied
///     (or <c>null</c> when the signal was missing), whether the term matched,
///     how long the lookup + compare took, and -- when the term failed -- a
///     human-readable reason the explainer panel renders verbatim.
/// </summary>
/// <param name="Facet">Signal key that was probed (the term's <see cref="Predicate.Term.Facet"/>).</param>
/// <param name="Op">Operator applied (the term's <see cref="Predicate.Term.Op"/>).</param>
/// <param name="Value">Authored value the term compared against.</param>
/// <param name="ActualFacetValue">What the signals dictionary returned for <paramref name="Facet"/>; <c>null</c> when the facet was missing entirely.</param>
/// <param name="Matched"><c>true</c> when the term evaluated true on this run.</param>
/// <param name="EvalTicks">
///     <see cref="TimeSpan"/>-tick cost of the term's lookup + comparison. Zero
///     for terms that were short-circuited (an earlier <c>And</c> child failed
///     or an earlier <c>Or</c> child won).
/// </param>
/// <param name="FailureReason">
///     Human-readable explanation when <paramref name="Matched"/> is <c>false</c>.
///     <c>null</c> when the term matched. Short-circuit skips carry a
///     <c>"skipped -- earlier And child failed"</c> / <c>"skipped -- earlier
///     Or child won"</c> sentinel so the panel can style them differently.
/// </param>
public sealed record PredicateTermOutcome(
    string Facet,
    PredicateOp Op,
    object Value,
    object? ActualFacetValue,
    bool Matched,
    long EvalTicks,
    string? FailureReason);

/// <summary>
///     Result of a <see cref="PredicateTraceEvaluator.Trace"/> call: the
///     overall match verdict (identical to what
///     <see cref="PredicateEvaluator.Evaluate"/> would have returned), the
///     per-term outcomes in logical evaluation order, and the sum of every
///     term's lookup + compare ticks.
/// </summary>
/// <param name="OverallMatched">Whether the predicate would have evaluated true.</param>
/// <param name="Terms">Depth-first per-term outcomes in logical evaluation order. Includes short-circuited siblings flagged via <see cref="PredicateTermOutcome.FailureReason"/>.</param>
/// <param name="TotalTicks">Sum of <see cref="PredicateTermOutcome.EvalTicks"/> across <paramref name="Terms"/>.</param>
public sealed record PredicateTrace(
    bool OverallMatched,
    IReadOnlyList<PredicateTermOutcome> Terms,
    long TotalTicks);
