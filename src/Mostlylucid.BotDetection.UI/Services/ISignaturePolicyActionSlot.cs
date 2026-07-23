using Microsoft.AspNetCore.Html;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Contribution seam rendered on the signature-detail HEADER "Policy:" row. A commercial
///     package registers the real implementation -- the top policy-action block
///     (Allow / Block / Throttle / Transform, wired to the operator-action pipe as a real
///     scoped PolicyRule) -- so the edit-controls moat sits at the top of the detail page.
///     The FOSS default renders nothing; the page host is always safe to call.
///     <para>
///     Companion to <see cref="IFingerprintNameActionSlot" /> (the inline per-name affordance);
///     this one is the page-level policy block. Same discipline: resolved ONCE via singleton
///     constructor injection and called synchronously -- never per-render DI resolution or a
///     <c>Component.InvokeAsync</c> round-trip -- so any license/auth check the commercial
///     implementation needs happens once per request (e.g. memoized on <c>HttpContext.Items</c>),
///     never per render.
///     </para>
/// </summary>
public interface ISignaturePolicyActionSlot
{
    /// <summary>
    ///     Returns the HTML for the header policy-action block. The FOSS default returns empty --
    ///     always safe to call, never gated by license/commercial checks in the caller; the
    ///     returned content itself decides whether to render an enabled or disabled affordance.
    ///     Must never return null.
    /// </summary>
    /// <param name="signatureId">The signature/fingerprint id the detail page is showing.</param>
    /// <param name="currentAction">The current effective action/policy name (the page model's <c>Action</c>), or null when none is set.</param>
    /// <param name="botType">The classified bot type/family, or null for an unclassified/human visitor. Already on the page model -- never fetched here.</param>
    /// <param name="isBot">Whether this signature is currently classified as a bot.</param>
    IHtmlContent Render(string signatureId, string? currentAction, string? botType, bool isBot);
}

/// <summary>
///     FOSS default: empty. Registered via <c>TryAdd</c> so a commercial host's real
///     implementation wins without a replace-registration.
/// </summary>
public sealed class EmptySignaturePolicyActionSlot : ISignaturePolicyActionSlot
{
    /// <inheritdoc />
    public IHtmlContent Render(string signatureId, string? currentAction, string? botType, bool isBot)
        => HtmlString.Empty;
}
