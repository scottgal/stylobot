namespace Mostlylucid.BotDetection.ApiHolodeck.Models;

/// <summary>
///     Configuration options for the Holodeck extension.
/// </summary>
public class HolodeckOptions
{
    /// <summary>
    ///     Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "BotDetection:Holodeck";

    // ==========================================
    // MockLLMApi Configuration
    // ==========================================

    /// <summary>
    ///     Base URL for the MockLLMApi server.
    ///     Default: http://localhost:5116/api/mock
    /// </summary>
    public string MockApiBaseUrl { get; set; } = "http://localhost:5116/api/mock";

    /// <summary>
    ///     Holodeck mode determining how fake responses are generated.
    /// </summary>
    public HolodeckMode Mode { get; set; } = HolodeckMode.RealisticButUseless;

    /// <summary>
    ///     Source for generating context keys to maintain consistent fake worlds per bot.
    /// </summary>
    public ContextSource ContextSource { get; set; } = ContextSource.Fingerprint;

    /// <summary>
    ///     Maximum requests to study a bot before hard-blocking.
    ///     Set to 0 to disable cutoff.
    ///     Default: 50
    /// </summary>
    public int MaxStudyRequests { get; set; } = 50;

    /// <summary>
    ///     Timeout for MockLLMApi requests in milliseconds.
    ///     Default: 5000ms
    /// </summary>
    public int MockApiTimeoutMs { get; set; } = 5000;

    // ==========================================
    // Honeypot Link Detection
    // ==========================================

    /// <summary>
    ///     Enable honeypot link detection (trap paths that bots follow).
    ///     Default: true
    /// </summary>
    public bool EnableHoneypotLinkDetection { get; set; } = true;

    /// <summary>
    ///     Paths that are considered honeypot traps.
    ///     Bots that access these paths are immediately flagged.
    /// </summary>
    public List<string> HoneypotPaths { get; set; } = new()
    {
        "/admin-secret",
        "/wp-login.php",
        "/wp-admin",
        "/.env",
        "/xmlrpc.php",
        "/phpmyadmin",
        "/.git/config",
        "/config.php",
        "/backup.sql",
        "/debug.php"
    };

    /// <summary>
    ///     Whether to inject hidden honeypot links into HTML responses.
    ///     Default: false
    /// </summary>
    public bool InjectHiddenLinks { get; set; } = false;

    /// <summary>
    ///     CSS class for injected hidden links.
    ///     Default: honeypot-link
    /// </summary>
    public string HiddenLinkClass { get; set; } = "honeypot-link";

    // ==========================================
    // Project Honeypot Reporting
    // ==========================================

    /// <summary>
    ///     Enable reporting detected bots to Project Honeypot.
    ///     Requires ProjectHoneypotAccessKey to be set.
    ///     Default: false
    /// </summary>
    public bool ReportToProjectHoneypot { get; set; } = false;

    /// <summary>
    ///     Project Honeypot API access key for reporting.
    ///     Get a free key at https://www.projecthoneypot.org/
    /// </summary>
    public string? ProjectHoneypotAccessKey { get; set; }

    /// <summary>
    ///     Minimum risk score required to report to Project Honeypot.
    ///     Default: 0.85
    /// </summary>
    public double MinRiskToReport { get; set; } = 0.85;

    /// <summary>
    ///     Maximum reports per hour to avoid flooding Project Honeypot.
    ///     Default: 100
    /// </summary>
    public int MaxReportsPerHour { get; set; } = 100;

    /// <summary>
    ///     Visitor types to report to Project Honeypot.
    /// </summary>
    public List<ReportableVisitorType> ReportVisitorTypes { get; set; } = new()
    {
        ReportableVisitorType.Harvester,
        ReportableVisitorType.CommentSpammer,
        ReportableVisitorType.Suspicious
    };
}

/// <summary>
///     Holodeck response modes.
/// </summary>
public enum HolodeckMode
{
    /// <summary>
    ///     Generate realistic-looking fake data.
    /// </summary>
    Realistic,

    /// <summary>
    ///     Generate realistic-looking but useless data (wrong schemas, demo values).
    /// </summary>
    RealisticButUseless,

    /// <summary>
    ///     Generate chaotic responses with errors, timeouts, and inconsistencies.
    /// </summary>
    Chaos,

    /// <summary>
    ///     Generate responses matching a strict schema (for OpenAPI-based fakes).
    /// </summary>
    StrictSchema,

    /// <summary>
    ///     Mix of modes - keeps bots guessing.
    /// </summary>
    Adversarial
}

/// <summary>
///     Source for generating context keys.
/// </summary>
public enum ContextSource
{
    /// <summary>
    ///     Use browser fingerprint as context key.
    /// </summary>
    Fingerprint,

    /// <summary>
    ///     Use client IP address as context key.
    /// </summary>
    Ip,

    /// <summary>
    ///     Use session ID as context key.
    /// </summary>
    Session,

    /// <summary>
    ///     Use combination of fingerprint + IP.
    /// </summary>
    Combined
}

/// <summary>
///     Visitor types that can be reported to Project Honeypot.
/// </summary>
public enum ReportableVisitorType
{
    /// <summary>
    ///     Email address harvester.
    /// </summary>
    Harvester,

    /// <summary>
    ///     Comment spammer.
    /// </summary>
    CommentSpammer,

    /// <summary>
    ///     Generally suspicious behavior.
    /// </summary>
    Suspicious
}