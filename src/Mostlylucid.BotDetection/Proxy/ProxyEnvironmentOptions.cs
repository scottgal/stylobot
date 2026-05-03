namespace Mostlylucid.BotDetection.Proxy;

/// <summary>
/// Configuration for proxy topology sensing.
/// Bind via: BotDetection:ProxyEnvironment in appsettings.json.
/// </summary>
public class ProxyEnvironmentOptions
{
    /// <summary>
    /// Override auto-detection with a fixed topology.
    /// Accepted values: Auto, Direct, Cloudflare, CloudFront, Fastly, Nginx, Generic.
    /// Default: Auto (infers topology from headers on first request).
    /// </summary>
    public string Mode { get; set; } = "Auto";

    internal bool IsAutoMode =>
        string.IsNullOrEmpty(Mode) || Mode.Equals("Auto", StringComparison.OrdinalIgnoreCase);

    internal ProxyTopology? ParsedMode =>
        IsAutoMode ? null :
        Enum.TryParse<ProxyTopology>(Mode, ignoreCase: true, out var t) ? t : null;
}
