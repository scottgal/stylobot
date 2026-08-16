using Microsoft.AspNetCore.Html;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Contribution seam rendered inline next to every fingerprint/signature NAME the
///     dashboard shows (visitor list rows, Top Bots, the signature-detail header, the
///     "looks like" similar-signatures list, the visitor card). A commercial package
///     registers the real implementation -- a quick allow/block action affordance --
///     so the edit-controls moat is pervasive rather than buried in one detail panel.
///     <para>
///     Mirrors <see cref="IEffectivePolicyConfigOverlay" />'s shape, not
///     <c>IDashboardContribution</c>/<c>DashboardContributionSlot</c>: this fires dozens
///     of times per page render (once per name), so it is resolved ONCE via constructor
///     injection of a singleton and called synchronously -- never per-row DI resolution
///     or a <c>Component.InvokeAsync</c> round-trip. Being synchronous and argument-minimal
///     is deliberate: it forces any license/auth check the commercial implementation needs
///     to happen once per request (e.g. memoized on <c>HttpContext.Items</c> in its own
///     constructor/accessor), never per name.
///     </para>
/// </summary>
public interface IFingerprintNameActionSlot
{
    /// <summary>
    ///     Returns the HTML to render inline next to a fingerprint/signature name. The FOSS
    ///     default returns empty -- always safe to call, never gated by license/commercial
    ///     checks in the caller; the returned content itself decides whether to render an
    ///     enabled or disabled affordance. Must never return null.
    /// </summary>
    /// <param name="fingerprintId">The signature/fingerprint id the name belongs to.</param>
    /// <param name="displayName">The name as rendered at the call site (already resolved: custom name, LLM name, or UA-derived fallback).</param>
    /// <param name="botType">The classified bot type/family, or null for an unclassified/human visitor. Already available on the row/page model at every call site -- never fetched here.</param>
    /// <param name="botProbability">
    ///     The raw bot-probability score backing this row's verdict, or <c>null</c> when the
    ///     call site genuinely has no score available (e.g. the "looks like" nearest-fingerprint
    ///     list, which carries no classification for the neighbours it lists). The implementation
    ///     compares this against the canonical HumanCeiling/BotFloor thresholds to render one of
    ///     THREE states -- bot / human / uncertain -- never collapsed to a boolean. A confirmed
    ///     human and a genuinely unclassified/unknown row are different states and must not render
    ///     identically.
    /// </param>
    IHtmlContent Render(string fingerprintId, string displayName, string? botType, double? botProbability);
}

/// <summary>
///     FOSS default: empty. Registered via <c>TryAdd</c> so a commercial host's real
///     implementation wins without a replace-registration.
/// </summary>
public sealed class EmptyFingerprintNameActionSlot : IFingerprintNameActionSlot
{
    /// <inheritdoc />
    public IHtmlContent Render(string fingerprintId, string displayName, string? botType, double? botProbability)
        => HtmlString.Empty;
}
