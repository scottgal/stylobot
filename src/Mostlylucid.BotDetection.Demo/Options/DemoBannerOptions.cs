namespace Mostlylucid.BotDetection.Demo.Options;

/// <summary>
///     Tunables for the demo subdomain banner partial. Bound at
///     <c>Demo:Banner</c>. Every value has a baked-in default so the
///     demo still renders a sensible banner if the operator never
///     touches configuration.
/// </summary>
public sealed class DemoBannerOptions
{
    /// <summary>Master toggle. When false the banner partial renders nothing.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Main banner copy. Plain text, no markdown.</summary>
    public string Text { get; init; } = "Live demo of stylobot FOSS controls on a real ASP.NET app.";

    /// <summary>Link target for the inline "GitHub" word in the banner.</summary>
    public string SourceUrl { get; init; } = "https://github.com/scottgal/stylobot";

    /// <summary>Link target for the inline "paid pack" word in the banner.</summary>
    public string PackUrl { get; init; } = "https://stylobot.net/packs/aspnet";
}
