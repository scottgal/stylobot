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
/// </summary>
/// <param name="RuleId">Existing rule id; <c>null</c> when creating a new rule.</param>
/// <param name="Scope">Scope the rule is authored at.</param>
/// <param name="Priority">Priority within the scope; lower = higher.</param>
/// <param name="PredicateText">Canonical text from <see cref="Policies.Predicate.PredicateFormatter.Format"/>.</param>
/// <param name="ActionKind">Lower-case action kind: <c>"allow" / "observe" / "tag" / "challenge" / "ratelimit" / "block"</c>.</param>
/// <param name="ChallengeKind">Challenge kind metadata; only relevant when <see cref="ActionKind"/> is <c>"challenge"</c>.</param>
/// <param name="TagName">Tag name metadata; only relevant when <see cref="ActionKind"/> is <c>"tag"</c>.</param>
/// <param name="RequestsPerMinute">Rate-limit metadata; only relevant when <see cref="ActionKind"/> is <c>"ratelimit"</c>.</param>
/// <param name="Mode">Lifecycle mode for the rule.</param>
/// <param name="Notes">Operator-authored note string.</param>
/// <param name="SubmitUrl">URL the form posts/puts to (commercial mutation API).</param>
/// <param name="HttpMethod">"POST" for create, "PUT" for update.</param>
/// <param name="CancelUrl">Where the row swaps back to on Cancel.</param>
public sealed record PolicyEditRowViewModel(
    Guid? RuleId,
    PolicyScope Scope,
    int Priority,
    string PredicateText,
    string ActionKind,
    string? ChallengeKind,
    string? TagName,
    int? RequestsPerMinute,
    PolicyMode Mode,
    string Notes,
    string SubmitUrl,
    string HttpMethod,
    string CancelUrl);
