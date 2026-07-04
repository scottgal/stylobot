using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     FOSS dashboard placeholder for the license card. FOSS has no license
///     concept — this model just carries the base path so the partial view
///     can render a small "Upgrade to Commercial" link. Commercial replaces
///     the rendered partial view via view-location precedence and swaps in
///     its own full-featured license card.
/// </summary>
public sealed class LicenseCardModel
{
    public required string BasePath { get; init; }

    /// <summary>Where the upsell link points.</summary>
    public string UpsellUrl { get; init; } = "https://stylo.bot/pricing";

    /// <summary>Displayed as the link text.</summary>
    public string UpsellLabel { get; init; } = "Upgrade to Commercial";

    /// <summary>
    ///     FOSS status is always <see cref="LicenseStatusKind.Unconfigured"/>.
    ///     Kept for source-compat with downstream partial views + callers that
    ///     check for the commercial-active state.
    /// </summary>
    public LicenseStatusKind Status { get; init; } = LicenseStatusKind.Unconfigured;
}

/// <summary>
///     Minimal builder for the FOSS upsell-link placeholder. The HttpContext
///     parameter is preserved so callers don't need to change; the FOSS
///     implementation ignores it. Commercial can shadow via DI / view
///     precedence to build a full LicenseCardModel from license state.
/// </summary>
public static class LicenseCardModelBuilder
{
    public static LicenseCardModel Build(HttpContext context, string basePath) =>
        new() { BasePath = basePath };
}

/// <summary>
///     Kept as a stub so downstream call sites that check for
///     Active/Trial can still compile in FOSS. All values collapse to
///     <see cref="Unconfigured"/> at runtime under FOSS — the "is
///     commercial active?" check evaluates false.
/// </summary>
public enum LicenseStatusKind
{
    /// <summary>Only state produced by the FOSS builder.</summary>
    Unconfigured,

    /// <summary>Commercial-only. FOSS never produces this.</summary>
    Active,

    /// <summary>Commercial-only. FOSS never produces this.</summary>
    Trial,

    /// <summary>Commercial-only. FOSS never produces this.</summary>
    Grace,

    /// <summary>Commercial-only. FOSS never produces this.</summary>
    Expired,

    /// <summary>Commercial-only. FOSS never produces this.</summary>
    DomainMismatchOnly
}