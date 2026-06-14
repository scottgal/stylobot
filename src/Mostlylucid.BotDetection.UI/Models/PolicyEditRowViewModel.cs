using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for the C6 expression editor. Drives <c>_EditRow.cshtml</c>
///     and the four panes it wraps (chip pane, expression pane, action selector,
///     mode + notes). Built by <see cref="Services.PolicyEditPresenter"/>.
///
///     The editor lives in FOSS so a self-hosted operator who buys nothing
///     can still write rules into their YAML store; the SUBMIT path is the
///     same commercial mutation API (<c>/api/v1/policies</c>, C3) so a
///     gateway running standalone gets a no-op 404 here -- which is the
///     correct behaviour for "no commercial control plane present".
///
///     <para>
///         The action-shape carrying fields are per-kind slices
///         (<see cref="Tag"/>, <see cref="Challenge"/>, <see cref="RateLimit"/>,
///         <see cref="Throttle"/>): exactly one is non-null on a populated row,
///         which one depends on <see cref="ActionKind"/>. The traffic-shaping
///         editor partials dispatch off of these slices; zero-field actions
///         (<c>allow</c> / <c>observe</c> / <c>block</c>) carry no slice -- the
///         kind string is enough.
///     </para>
/// </summary>
/// <param name="RuleId">Existing rule id; <c>null</c> when creating a new rule.</param>
/// <param name="Scope">Scope the rule is authored at.</param>
/// <param name="Priority">Priority within the scope; lower = higher.</param>
/// <param name="PredicateText">Canonical text from <see cref="Policies.Predicate.PredicateFormatter.Format"/>.</param>
/// <param name="ActionKind">Lower-case action kind: <c>"allow" / "observe" / "tag" / "challenge" / "ratelimit" / "throttle" / "block"</c>.</param>
/// <param name="Tag">Populated when <see cref="ActionKind"/> is <c>"tag"</c>.</param>
/// <param name="Challenge">Populated when <see cref="ActionKind"/> is <c>"challenge"</c>.</param>
/// <param name="RateLimit">Populated when <see cref="ActionKind"/> is <c>"ratelimit"</c>.</param>
/// <param name="Throttle">Populated when <see cref="ActionKind"/> is <c>"throttle"</c>.</param>
/// <param name="Mode">Lifecycle mode for the rule.</param>
/// <param name="Notes">Operator-authored note string.</param>
/// <param name="SubmitUrl">URL the form posts/puts to (commercial mutation API).</param>
/// <param name="HttpMethod">"POST" for create, "PUT" for update.</param>
/// <param name="CancelUrl">Where the row swaps back to on Cancel.</param>
/// <param name="Backtest">
///     Optional C8 backtest panel data. <c>null</c> on the initial render
///     (the placeholder copy renders); the JS posts the candidate to
///     <c>/dashboard/policystack/backtest</c> 500ms after each successful
///     parse and outerHTML-swaps the panel with a populated copy.
/// </param>
public sealed record PolicyEditRowViewModel(
    Guid? RuleId,
    PolicyScope Scope,
    int Priority,
    string PredicateText,
    string ActionKind,
    TagActionEdit? Tag,
    ChallengeActionEdit? Challenge,
    RateLimitActionEdit? RateLimit,
    ThrottleActionEdit? Throttle,
    PolicyMode Mode,
    string Notes,
    string SubmitUrl,
    string HttpMethod,
    string CancelUrl,
    PolicyBacktestViewModel? Backtest = null);