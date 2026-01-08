using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Known bot signatures and patterns
///     Based on lists from matomo.org, Cloudflare, and other sources
/// </summary>
public static partial class BotSignatures
{
    /// <summary>
    ///     Known good bots (search engines, monitoring tools, etc.)
    /// </summary>
    public static readonly Dictionary<string, string> GoodBots = new(StringComparer.OrdinalIgnoreCase)
    {
        // Search Engines
        ["Googlebot"] = "Google Search",
        ["Googlebot-Image"] = "Google Image Search",
        ["Googlebot-News"] = "Google News",
        ["Googlebot-Video"] = "Google Video Search",
        ["Google-InspectionTool"] = "Google Search Console",
        ["Google-Site-Verification"] = "Google Site Verification",
        ["Storebot-Google"] = "Google Store Bot",
        ["Bingbot"] = "Bing Search",
        ["msnbot"] = "MSN Search",
        ["BingPreview"] = "Bing Preview",
        ["Slurp"] = "Yahoo Search",
        ["DuckDuckBot"] = "DuckDuckGo",
        ["Baiduspider"] = "Baidu Search",
        ["YandexBot"] = "Yandex Search",
        ["Sogou"] = "Sogou Search",
        ["Exabot"] = "Exalead Search",

        // Social Media
        ["facebookexternalhit"] = "Facebook",
        ["facebot"] = "Facebook Bot",
        ["Twitterbot"] = "Twitter",
        ["LinkedInBot"] = "LinkedIn",
        ["Slackbot"] = "Slack",
        ["Discordbot"] = "Discord",
        ["TelegramBot"] = "Telegram",
        ["WhatsApp"] = "WhatsApp",

        // SEO & Monitoring
        ["AhrefsBot"] = "Ahrefs SEO",
        ["SemrushBot"] = "SEMrush",
        ["MJ12bot"] = "Majestic SEO",
        ["DotBot"] = "Moz",
        ["Screaming Frog"] = "Screaming Frog SEO",
        ["SEOkicks"] = "SEOkicks",
        ["Uptimebot"] = "Uptime Monitor",
        ["UptimeRobot"] = "UptimeRobot Monitor",
        ["StatusCake"] = "StatusCake Monitor",
        ["Pingdom"] = "Pingdom Monitor",

        // AI/LLM Crawlers
        ["GPTBot"] = "OpenAI GPT",
        ["ChatGPT-User"] = "ChatGPT User",
        ["OAI-SearchBot"] = "OpenAI Search",
        ["ClaudeBot"] = "Anthropic Claude",
        ["Claude-Web"] = "Anthropic Claude Web",
        ["PerplexityBot"] = "Perplexity AI",
        ["Bytespider"] = "ByteDance AI",
        ["cohere-ai"] = "Cohere AI",

        // Archives & Research
        ["ia_archiver"] = "Internet Archive",
        ["archive.org_bot"] = "Internet Archive",
        ["Amazonbot"] = "Amazon",
        ["AppleBot"] = "Apple",
        ["Applebot-Extended"] = "Apple Extended",

        // Development & Testing
        ["curl"] = "cURL",
        ["Wget"] = "GNU Wget",
        ["python-requests"] = "Python Requests",
        ["Postman"] = "Postman",
        ["Insomnia"] = "Insomnia"
    };

    /// <summary>
    ///     Known malicious or suspicious bot patterns
    /// </summary>
    public static readonly HashSet<string> MaliciousBotPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "scrapy", "scraper", "spider", "crawler", "bot", "httrack", "harvest",
        "extract", "grab", "suck", "reaper", "ninja", "leech", "sucker",
        "mass", "download", "fetch", "go-http-client", "Java/", "python-urllib",
        "libwww-perl", "urllib", "pycurl", "libcurl", "Mechanize", "WWW-Mechanize",
        "NetcraftSurveyAgent", "ZmEu", "sqlmap", "nikto", "scan", "nmap",
        "masscan", "nessus", "openvas", "acunetix", "vega/", "metis", "nsauditor",
        "paros", "webshag", "w3af", "dirbuster", "havij", "zmeu"
    };

    /// <summary>
    ///     Suspicious header combinations that indicate automation
    /// </summary>
    public static readonly List<string[]> SuspiciousHeaderPatterns = new()
    {
        // Missing common browser headers
        new[] { "User-Agent", "!Accept-Language" },
        new[] { "User-Agent", "!Accept-Encoding" },
        new[] { "User-Agent", "!Accept" }

        // Uncommon header orders (browsers send headers in consistent order)
        // This would be checked programmatically
    };

    /// <summary>
    ///     Common automation frameworks
    /// </summary>
    public static readonly HashSet<string> AutomationFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Selenium", "WebDriver", "PhantomJS", "HeadlessChrome", "Puppeteer",
        "Playwright", "Cypress", "Nightmare", "CasperJS", "SlimerJS",
        "Zombie.js", "TestCafe", "Watir", "Mechanize", "Scrapy",
        "BeautifulSoup", "jsdom", "cheerio", "HttpClient", "RestSharp"
    };

    /// <summary>
    ///     Regex patterns for bot detection in User-Agent (string form for backwards compatibility)
    /// </summary>
    public static readonly string[] BotPatterns =
    [
        @"\bbot\b", @"\bcrawl", @"\bspider\b", @"\bslurp\b", @"\barchive\b",
        @"\bindex", @"\bscrape", @"\bfetch\b", @"http:\/\/", @"https:\/\/",
        @"\+http", @"@", @"\.com", @"\.org", @"\.net",
        @"^\w+\/[\d\.]+$" // Simple version patterns like "curl/7.68.0"
    ];

    // ==========================================
    // Source-Generated Regex Patterns (fastest)
    // ==========================================
    // These are compiled at build time for maximum performance.
    // Use CompiledBotPatterns for hot paths instead of BotPatterns.

    /// <summary>
    ///     Pre-compiled regex patterns for bot detection.
    ///     Use these instead of BotPatterns for best performance.
    /// </summary>
    public static readonly Regex[] CompiledBotPatterns =
    [
        BotRegex(),
        CrawlRegex(),
        SpiderRegex(),
        SlurpRegex(),
        ArchiveRegex(),
        IndexRegex(),
        ScrapeRegex(),
        FetchRegex(),
        HttpRegex(),
        HttpsRegex(),
        PlusHttpRegex(),
        AtSymbolRegex(),
        DotComRegex(),
        DotOrgRegex(),
        DotNetRegex(),
        SimpleVersionRegex()
    ];

    // Source-generated regex methods - compiled at build time
    [GeneratedRegex(@"\bbot\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex BotRegex();

    [GeneratedRegex(@"\bcrawl", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex CrawlRegex();

    [GeneratedRegex(@"\bspider\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex SpiderRegex();

    [GeneratedRegex(@"\bslurp\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex SlurpRegex();

    [GeneratedRegex(@"\barchive\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex ArchiveRegex();

    [GeneratedRegex(@"\bindex", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex IndexRegex();

    [GeneratedRegex(@"\bscrape", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex ScrapeRegex();

    [GeneratedRegex(@"\bfetch\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex FetchRegex();

    [GeneratedRegex(@"http://", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex HttpRegex();

    [GeneratedRegex(@"https://", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex HttpsRegex();

    [GeneratedRegex(@"\+http", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex PlusHttpRegex();

    [GeneratedRegex(@"@", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex AtSymbolRegex();

    [GeneratedRegex(@"\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex DotComRegex();

    [GeneratedRegex(@"\.org", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex DotOrgRegex();

    [GeneratedRegex(@"\.net", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex DotNetRegex();

    [GeneratedRegex(@"^\w+\/[\d\.]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled, 100)]
    private static partial Regex SimpleVersionRegex();
}