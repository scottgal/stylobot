namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     Configuration for the arcjet well-known-bots catalog download.
///     Bind from <c>BotDetection:WellKnownBots</c> in appsettings.json.
/// </summary>
public sealed class WellKnownBotsOptions
{
    /// <summary>
    ///     URL of the well-known-bots JSON catalog. Defaults to the official
    ///     arcjet repository. Set to empty to disable downloads entirely;
    ///     the index will be empty until a successful fetch.
    /// </summary>
    public string Url { get; set; } =
        "https://raw.githubusercontent.com/arcjet/well-known-bots/main/well-known-bots.json";

    /// <summary>
    ///     How often to refresh the catalog. Default 24 hours.
    ///     Minimum enforced at 1 hour by the refresh service.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     Hard cap on the downloaded response size. Default 2 MB.
    ///     Prevents a malicious mirror from feeding a large payload.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
}