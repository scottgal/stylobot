using System.Diagnostics;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Evaluation;

/// <summary>
///     Instrumented rule evaluation loop for the authored
///     <see cref="PolicyRule"/> grammar (A4-A6). Walks an ordered list of
///     effective rules, evaluates each rule's predicate exactly once with
///     stopwatch timing, and writes one <see cref="PolicyDecision"/> row per
///     rule to the supplied <see cref="IPolicyDecisionLog"/>.
/// </summary>
/// <remarks>
///     <para>
///         The loop is short-circuit on the enforcement side: the first
///         <see cref="PolicyMode.Live"/> rule whose predicate matches becomes
///         the winner and no later rule's predicate is evaluated. Rules
///         skipped after a winner is found are still recorded -- their row
///         carries <c>Matched=false</c> and <c>EvalLatencyTicks=0</c> so the
///         explainer trace can render "not evaluated -- earlier rule won".
///     </para>
///     <para>
///         <see cref="PolicyMode.Observe"/> rules are evaluated and logged
///         like Live rules, but their match does NOT count as the winner --
///         a subsequent Live rule that matches will own the enforcement.
///         Observe mode is the shadow posture used when authoring a new
///         rule against live traffic before flipping it to Live.
///     </para>
///     <para>
///         <see cref="PolicyMode.Draft"/> and <see cref="PolicyMode.Disabled"/>
///         rules are skipped entirely -- no row is written, no predicate is
///         evaluated. They exist on disk but are invisible to the request
///         path.
///     </para>
///     <para>
///         <see cref="Stopwatch.GetElapsedTime(long)"/> is used to convert the
///         high-resolution timestamp delta to a <see cref="TimeSpan"/>, whose
///         <c>Ticks</c> are written verbatim into
///         <see cref="PolicyDecision.EvalLatencyTicks"/>. The
///         <see cref="PolicyDecisionAggregate"/> percentile math in A6 assumes
///         TimeSpan ticks (100ns each, 10 per microsecond) -- writing raw
///         <see cref="Stopwatch.GetTimestamp"/> deltas would inflate the
///         percentiles by ~100x on macOS where
///         <see cref="Stopwatch.Frequency"/> is ~1e9.
///     </para>
/// </remarks>
public sealed class RuleEvaluationLoop
{
    private readonly IPolicyDecisionLog _log;

    public RuleEvaluationLoop(IPolicyDecisionLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    ///     Evaluate <paramref name="effective"/> rules against
    ///     <paramref name="requestSignals"/> and return the first Live rule
    ///     whose predicate matched, or <c>null</c> when no rule won.
    /// </summary>
    /// <param name="effective">
    ///     Rules in evaluation order (typically resolver output:
    ///     most-specific-scope first, priority ascending within a scope).
    /// </param>
    /// <param name="requestSignals">
    ///     Signal facet name → value map. Unknown facets in a predicate
    ///     silently fail per <see cref="PredicateEvaluator"/> semantics.
    /// </param>
    /// <param name="requestFingerprint">
    ///     Optional fingerprint id to attach to every decision row, so the
    ///     explainer's "replay this request" panel can find them.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the decision log writes.</param>
    public async Task<PolicyRule?> EvaluateAsync(
        IReadOnlyList<PolicyRule> effective,
        IReadOnlyDictionary<string, object?> requestSignals,
        string? requestFingerprint = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(requestSignals);

        // Buffer decisions so WinnerRuleId can be back-filled in one pass
        // after the loop. We can't know the winner's id at the moment we
        // log a row, and PolicyDecision.WinnerRuleId is on the settled A6
        // surface -- it must be the same id on every row from one
        // evaluation for the explainer trace to render correctly.
        var pending = new List<PolicyDecision>(effective.Count);
        PolicyRule? winner = null;
        var now = DateTimeOffset.UtcNow;

        foreach (var rule in effective)
        {
            // Draft + Disabled are invisible to the request path entirely.
            if (rule.Mode is PolicyMode.Draft or PolicyMode.Disabled)
                continue;

            if (winner is not null)
            {
                // Short-circuit: a Live rule already won. Record a SKIPPED
                // row so the trace shows this rule never got the chance to
                // match.
                pending.Add(new PolicyDecision(
                    RuleId: rule.Id,
                    WinnerRuleId: Guid.Empty, // back-filled below
                    Matched: false,
                    RequestFingerprint: requestFingerprint,
                    Action: rule.Action,
                    Mode: rule.Mode,
                    EvalLatencyTicks: 0,
                    ObservedAt: now));
                continue;
            }

            var start = Stopwatch.GetTimestamp();
            var matched = PredicateEvaluator.Evaluate(rule.Predicate, requestSignals);
            var elapsed = Stopwatch.GetElapsedTime(start);

            pending.Add(new PolicyDecision(
                RuleId: rule.Id,
                WinnerRuleId: Guid.Empty, // back-filled below
                Matched: matched,
                RequestFingerprint: requestFingerprint,
                Action: rule.Action,
                Mode: rule.Mode,
                EvalLatencyTicks: elapsed.Ticks,
                ObservedAt: now));

            // Observe mode matches are recorded but do not stop the loop --
            // a subsequent Live rule that matches owns the enforcement.
            // Only Live matches become the winner.
            if (matched && rule.Mode == PolicyMode.Live)
                winner = rule;
        }

        var winnerId = winner?.Id ?? Guid.Empty;
        foreach (var decision in pending)
            await _log.AppendAsync(decision with { WinnerRuleId = winnerId }, ct).ConfigureAwait(false);

        return winner;
    }
}
