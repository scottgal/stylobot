namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Default URLs for authoritative bot detection lists.
///     These are used as defaults when not overridden in configuration.
///     All URLs can be configured via appsettings.json under BotDetection:DataSources.
/// </summary>
public static class BotListSources
{
    // ==========================================
    // Bot Pattern Sources (User-Agent matching)
    // ==========================================

    /// <summary>
    ///     IsBot patterns from omrilotan/isbot (JSON array of regex patterns)
    ///     Most comprehensive source - aggregates patterns from:
    ///     - crawler-user-agents (monperrus)
    ///     - matomo.org device detector
    ///     - myip.ms crawler database
    ///     - top-crawler-agents
    ///     - Manual curated additions
    ///     https://github.com/omrilotan/isbot
    /// </summary>
    public const string IsBotPatterns =
        "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json";

    /// <summary>
    ///     Matomo Device Detector bot list (YAML format)
    ///     Contains 1000+ bot patterns with categories and metadata.
    ///     Note: isbot already incorporates these patterns, enable if you need category info.
    ///     https://github.com/matomo-org/device-detector
    /// </summary>
    public const string MatomoBotList =
        "https://raw.githubusercontent.com/matomo-org/device-detector/master/regexes/bots.yml";

    /// <summary>
    ///     Crawler User Agents (JSON format)
    ///     Community-maintained list of known crawlers with URLs.
    ///     Note: isbot already incorporates these patterns.
    ///     https://github.com/monperrus/crawler-user-agents
    /// </summary>
    public const string CrawlerUserAgents =
        "https://raw.githubusercontent.com/monperrus/crawler-user-agents/master/crawler-user-agents.json";

    // ==========================================
    // IP Range Sources (Datacenter detection)
    // ==========================================

    /// <summary>
    ///     AWS IP ranges (JSON format)
    ///     Official list from Amazon - updated frequently.
    ///     https://docs.aws.amazon.com/general/latest/gr/aws-ip-ranges.html
    /// </summary>
    public const string AwsIpRanges = "https://ip-ranges.amazonaws.com/ip-ranges.json";

    /// <summary>
    ///     Google Cloud IP ranges (JSON format)
    ///     Official list from Google.
    ///     https://cloud.google.com/compute/docs/faq#find_ip_range
    /// </summary>
    public const string GcpIpRanges = "https://www.gstatic.com/ipranges/cloud.json";

    /// <summary>
    ///     Azure IP ranges (JSON format)
    ///     Official list from Microsoft - requires download page scraping.
    ///     https://www.microsoft.com/en-us/download/details.aspx?id=56519
    ///     Note: This URL changes weekly, so it's disabled by default.
    /// </summary>
    public const string AzureIpRanges = "";

    /// <summary>
    ///     Cloudflare IPv4 ranges (text format, one CIDR per line)
    ///     Official list from Cloudflare.
    ///     https://www.cloudflare.com/ips/
    /// </summary>
    public const string CloudflareIpv4 = "https://www.cloudflare.com/ips-v4";

    /// <summary>
    ///     Cloudflare IPv6 ranges (text format, one CIDR per line)
    ///     Official list from Cloudflare.
    /// </summary>
    public const string CloudflareIpv6 = "https://www.cloudflare.com/ips-v6";

    /// <summary>
    ///     DigitalOcean IP ranges
    ///     No official public list available - disabled by default.
    /// </summary>
    public const string DigitalOceanIpRanges = "";
}