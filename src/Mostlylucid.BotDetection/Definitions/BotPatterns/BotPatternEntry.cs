namespace Mostlylucid.BotDetection.Definitions.BotPatterns;

/// <summary>
///     A single bot pattern entry loaded from a bot-patterns YAML file.
/// </summary>
public sealed record BotPatternEntry
{
    /// <summary>UA substring to match (case-insensitive).</summary>
    public string Pattern { get; init; } = "";

    /// <summary>Display name for UI and logging (e.g. "Googlebot", "FacebookBot").</summary>
    public string BotName { get; init; } = "";

    /// <summary>
    ///     Bot type string matching the <see cref="Models.BotType"/> enum.
    ///     Valid values: GoodBot, SearchEngine, SocialMediaBot, AiBot, MonitoringBot, Tool, Scraper, Unknown.
    /// </summary>
    public string BotType { get; init; } = "Unknown";

    /// <summary>Operator / company (e.g. "Google", "OpenAI").</summary>
    public string Vendor { get; init; } = "";

    /// <summary>
    ///     AI sub-category for AI bots. Only set when BotType is AiBot or GoodBot (for AI assistants/search).
    ///     Values: Training, Search, Assistant, ScrapingService.
    /// </summary>
    public string? AiCategory { get; init; }

    /// <summary>URL to fetch the bot's published CIDR IP range list (JSON).</summary>
    public string? IpRangesUrl { get; init; }

    /// <summary>FCrDNS domain patterns for identity verification (e.g. "*.googlebot.com").</summary>
    public string[]? VerifiedDomains { get; init; }
}

/// <summary>
///     Top-level structure of a bot-patterns YAML file.
/// </summary>
public sealed class BotPatternFile
{
    /// <summary>Category name (e.g. "search_engines", "ai_scrapers").</summary>
    public string Category { get; set; } = "";

    /// <summary>Human-readable description of this pattern group.</summary>
    public string? Description { get; set; }

    /// <summary>The bot patterns in this file.</summary>
    public List<BotPatternEntry> Patterns { get; set; } = [];
}
