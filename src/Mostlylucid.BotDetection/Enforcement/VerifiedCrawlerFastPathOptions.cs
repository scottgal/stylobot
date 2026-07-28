namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
/// Opt-in, host-scoped exemption from rate-limit/challenge latency for crawler requests that
/// have already been verified from server-derived identity evidence. Empty hosts disables it.
/// </summary>
public sealed class VerifiedCrawlerFastPathOptions
{
    /// <summary>Full TLS-served hosts allowed to use the fast path. Empty disables it.</summary>
    public List<string> MarketingHosts { get; set; } = [];

    /// <summary>Never fast-path these route prefixes, even for a verified crawler.</summary>
    public List<string> ExcludedPathPrefixes { get; set; } = ["/admin", "/api", "/dashboard", "/account"];
}
