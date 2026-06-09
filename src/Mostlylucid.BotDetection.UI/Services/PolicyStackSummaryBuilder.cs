using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Builds the rollup summary served by
///     <c>GET /api/v1/packs/policystack/summary</c> (B8 of the Pack Metrics +
///     Cohesive Views plan). The C1 dashboard overview row consumes this as a
///     machine-readable mirror of the SbStatTile the operator sees.
///     <para>
///         Counts are sourced from <see cref="IPolicyRuleStore"/> via
///         <see cref="IPolicyRuleStore.GetEffectiveRulesAsync"/> at the
///         <see cref="PolicyScope.Wildcard"/> scope path -- i.e. the global
///         policy baseline that applies to every request. Scope-specific
///         tuning (domain / subdomain / endpoint rules) lives in the per-scope
///         pages and is intentionally excluded from this global rollup.
///     </para>
///     <para>
///         The 15-minute decision count comes from
///         <see cref="IPolicyDecisionLog.StreamWindowAsync"/>. Cheapest path
///         that doesn't require extending the interface for one number; the
///         backtest runner uses the same surface so the cost characteristics
///         are already understood.
///     </para>
///     <para>
///         Both dependencies are optional per
///         <c>feedback_remote_mode_optional_di</c>:
///         <list type="bullet">
///             <item><description>
///                 <see cref="IPolicyRuleStore"/> null -> <see cref="BuildAsync"/>
///                 returns <c>null</c>; the endpoint translates that to
///                 <c>404 Not Found</c> with a one-line JSON body so the
///                 dashboard treats the row as a hard "not enabled" rather
///                 than fabricating zeros.
///             </description></item>
///             <item><description>
///                 <see cref="IPolicyDecisionLog"/> null -> the summary returns
///                 with <see cref="PolicyStackSummary.DecisionsLast15m"/> as
///                 <c>null</c>; mirrors the AspNet hub's "observation counter
///                 missing -> null tile" convention.
///             </description></item>
///         </list>
///     </para>
/// </summary>
public sealed class PolicyStackSummaryBuilder
{
    /// <summary>
    ///     Window used for the decision-count tile. Same 15-minute window the
    ///     AspNet hub uses for its observations tile so the cohesive-views
    ///     plan keeps a single short-window convention across packs.
    /// </summary>
    public static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(15);

    private static readonly IReadOnlyList<PolicyScope> WildcardPath =
        new PolicyScope[] { new PolicyScope.Wildcard() };

    private readonly IPolicyRuleStore? _ruleStore;
    private readonly IPolicyDecisionLog? _decisionLog;
    private readonly TimeProvider _timeProvider;

    public PolicyStackSummaryBuilder(
        IPolicyRuleStore? ruleStore = null,
        IPolicyDecisionLog? decisionLog = null,
        TimeProvider? timeProvider = null)
    {
        _ruleStore = ruleStore;
        _decisionLog = decisionLog;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Compose the summary. Returns <c>null</c> when no rule store is
    ///     registered on the host -- the calling endpoint translates that
    ///     into a 404. Never throws on the happy path; a decision-log read
    ///     exception falls through to the caller (the endpoint surfaces it
    ///     as a 500 like every other middleware route).
    /// </summary>
    public async Task<PolicyStackSummary?> BuildAsync(CancellationToken ct)
    {
        if (_ruleStore is null) return null;

        var rules = await _ruleStore.GetEffectiveRulesAsync(WildcardPath, ct).ConfigureAwait(false);

        var live = 0;
        var observe = 0;
        var draft = 0;
        var disabled = 0;
        foreach (var rule in rules)
        {
            switch (rule.Mode)
            {
                case PolicyMode.Live: live++; break;
                case PolicyMode.Observe: observe++; break;
                case PolicyMode.Draft: draft++; break;
                case PolicyMode.Disabled: disabled++; break;
            }
        }

        double? decisions = null;
        if (_decisionLog is not null)
        {
            decisions = await CountDecisionsAsync(_decisionLog, ShortWindow, ct).ConfigureAwait(false);
        }

        var band = (rules.Count, live) switch
        {
            (0, _) => HealthBand.Warning,
            (_, 0) => HealthBand.Caution,
            _ => HealthBand.Good
        };

        return new PolicyStackSummary(
            TotalRules: rules.Count,
            LiveRules: live,
            ObserveRules: observe,
            DraftRules: draft,
            DisabledRules: disabled,
            DecisionsLast15m: decisions,
            HealthBand: band,
            ComputedAtUtc: _timeProvider.GetUtcNow());
    }

    private static async Task<double> CountDecisionsAsync(
        IPolicyDecisionLog log,
        TimeSpan window,
        CancellationToken ct)
    {
        // StreamWindowAsync is the only IPolicyDecisionLog surface that returns
        // ALL decisions in a window (Aggregate is per-rule, ByFingerprint is
        // per-request). Counting via the stream avoids extending the interface
        // for one rollup number; the row count cap inside the stream (default
        // 100k rows) is more than enough headroom for a 15-minute window on a
        // single gateway.
        var count = 0L;
        await foreach (var _ in log.StreamWindowAsync(window, ct: ct).ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }
}

/// <summary>
///     Machine-readable rollup for <c>GET /api/v1/packs/policystack/summary</c>.
///     Mirrors the per-mode rule counts the dashboard surfaces on
///     <c>/dashboard/policies</c> plus a 15-minute decision count, all in a
///     shape the C1 overview row can render into an SbStatTile alongside the
///     AspNet pack's summary.
/// </summary>
/// <param name="TotalRules">
///     Total number of rules in the global (wildcard-scoped) policy stack.
/// </param>
/// <param name="LiveRules">
///     Rules in <see cref="PolicyMode.Live"/> -- actions actually applied.
/// </param>
/// <param name="ObserveRules">
///     Rules in <see cref="PolicyMode.Observe"/> -- matched + logged, action
///     suppressed.
/// </param>
/// <param name="DraftRules">
///     Rules in <see cref="PolicyMode.Draft"/> -- skipped by the evaluator.
/// </param>
/// <param name="DisabledRules">
///     Rules in <see cref="PolicyMode.Disabled"/> -- preserved on disk but
///     parked out of evaluation.
/// </param>
/// <param name="DecisionsLast15m">
///     Count of decision-log rows recorded in the last 15 minutes. <c>null</c>
///     when no <see cref="IPolicyDecisionLog"/> is registered (viewer-mode hosts
///     that pull data via REST).
/// </param>
/// <param name="HealthBand">
///     Overall posture: <see cref="HealthBand.Good"/> when at least one Live
///     rule is present, <see cref="HealthBand.Caution"/> when rules exist but
///     none are Live, <see cref="HealthBand.Warning"/> when the stack is
///     entirely empty.
/// </param>
/// <param name="ComputedAtUtc">Server timestamp at which the summary was computed.</param>
public sealed record PolicyStackSummary(
    int TotalRules,
    int LiveRules,
    int ObserveRules,
    int DraftRules,
    int DisabledRules,
    double? DecisionsLast15m,
    HealthBand HealthBand,
    DateTimeOffset ComputedAtUtc);
