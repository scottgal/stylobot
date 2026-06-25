namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Lightweight projection of a <see cref="Mostlylucid.BotDetection.Policies.Templates.Template"/>
///     for the policy-stack template gallery. Carries the operator-facing
///     fields plus a pre-classified <see cref="Intent"/> string so the
///     gallery and card partials can stay logic-free Razor.
///
///     <para>
///         Lives in the FOSS UI assembly because the gallery partial cannot
///         reference a commercial type — the commercial
///         <c>PolicyTemplatesController</c> projects into this shape and
///         hands it to the FOSS Razor view.
///     </para>
/// </summary>
/// <param name="Id">Stable template id, also used as the card's <c>data-template-id</c>.</param>
/// <param name="DisplayName">Operator-facing label rendered in the card head.</param>
/// <param name="Description">One-line summary, body copy of the card.</param>
/// <param name="Category">Grouping label (e.g. <c>protect</c>, <c>bootstrap</c>).</param>
/// <param name="Intent">
///     Lowercase <see cref="Mostlylucid.BotDetection.Policies.Rules.PolicyIntentKind"/>
///     name (e.g. <c>block</c>, <c>observe</c>, <c>throttle</c>). Drives the
///     intent-strip colour and icon on the card.
/// </param>
public sealed record TemplateSummaryViewModel(
    string Id,
    string DisplayName,
    string Description,
    string Category,
    string Intent);
