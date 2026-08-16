namespace Mostlylucid.BotDetection.InternalPlumbing;

/// <summary>
///     Configuration for recognising the product's OWN plumbing endpoints — the SignalR
///     dashboard hub and the client-side fingerprint beacon. Binds to
///     <c>BotDetection:InternalPlumbing</c> in the host configuration.
/// </summary>
/// <remarks>
///     <para>
///         Requests to these paths are classified <see cref="BotType.Internal"/> by the
///         ledger (mirroring the LAN-trust carve-out), so the dashboard's own live-update
///         channel can never read as a high-threat visitor, and the verdict is excluded
///         from the visitor risk feed. The hub is served by the gateway's own UI
///         middleware, so a path match identifies the product's plumbing — the same
///         trust shape as the health-endpoint recognition, but for the dashboard's
///         own surface.
///     </para>
///     <para>
///         Paths are matched case-insensitively at segment boundaries: a configured path
///         of <c>/stylobot/hub</c> matches both <c>/stylobot/hub</c> and
///         <c>/stylobot/hub/negotiate</c>, but NOT <c>/stylobot/hubspot</c>. This covers
///         SignalR's negotiate/invoke sub-paths without explicit enumeration.
///     </para>
/// </remarks>
public sealed class InternalPlumbingOptions
{
    public const string SectionName = "BotDetection:InternalPlumbing";

    /// <summary>
    ///     Path prefixes that identify the product's own plumbing endpoints. Each entry is
    ///     matched case-insensitively at segment boundaries by
    ///     <see cref="InternalPlumbingCatalog"/>. When empty after configuration binding,
    ///     the DI registration fills in <see cref="DefaultPaths"/> via PostConfigure.
    ///     Providing any value in <c>BotDetection:InternalPlumbing:Paths</c> replaces the
    ///     defaults entirely — a host serving the hub at a custom path (e.g. the
    ///     <c>STYLOBOT_HUB_PATH</c> override) MUST list that path here.
    /// </summary>
    public List<string> Paths { get; set; } = [];

    /// <summary>
    ///     The product's own plumbing paths: the default SignalR dashboard hub
    ///     (<c>/stylobot/hub</c>), its packaged/demo variant (<c>/_stylobot/hub</c>), and
    ///     the client-side fingerprint beacon (<c>/bot-detection/fingerprint</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPaths =
    [
        "/stylobot/hub",
        "/_stylobot/hub",
        "/bot-detection/fingerprint",
    ];

    /// <summary>Returns a new instance pre-populated with <see cref="DefaultPaths"/>.</summary>
    public static InternalPlumbingOptions Default => new() { Paths = new(DefaultPaths) };
}
