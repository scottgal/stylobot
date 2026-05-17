using VYaml.Annotations;

namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     A simulation pack defines a fake product installation (e.g., WordPress 5.9)
///     that acts as a honeypot for vulnerability scanners and exploit bots.
///     Includes honeypot paths, CVE probe signatures, and realistic response templates.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class SimulationPack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Framework { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>System prompt additions giving the LLM domain vocabulary and API style for this pack.</summary>
    public string? PromptPersonality { get; set; }

    public List<PackHoneypotPath> HoneypotPaths { get; set; } = [];
    public List<PackResponseTemplate> ResponseTemplates { get; set; } = [];
    public List<PackCveModule> CveModules { get; set; } = [];
    public PackTimingProfile TimingProfile { get; set; } = new();
}

/// <summary>
///     A honeypot path within a simulation pack.
///     Glob patterns are matched using FileSystemName.MatchesSimpleExpression.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PackHoneypotPath
{
    /// <summary>Glob pattern to match against request paths.</summary>
    public string Path { get; set; } = "";

    /// <summary>Confidence delta when this path is matched (0.0-1.0).</summary>
    public double Confidence { get; set; } = 0.9;

    /// <summary>Weight multiplier for the detection contribution.</summary>
    public double Weight { get; set; } = 2.0;

    /// <summary>Category label for grouping (e.g., "wordpress-auth").</summary>
    public string? Category { get; set; }
}

/// <summary>
///     A response template that the SimulationPackResponder or LLM API can serve for matched paths.
///     Static templates serve the Body directly. Dynamic templates provide hints for LLM generation.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PackResponseTemplate
{
    public string PathPattern { get; set; } = "";
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "text/html";
    public string Body { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
    public int MinDelayMs { get; set; }
    public int MaxDelayMs { get; set; } = 100;

    /// <summary>
    ///     When true, the Body is a prompt/description for LLM generation rather than static content.
    ///     The LLM API uses the body as context along with ResponseHints to generate dynamic responses.
    ///     Falls back to static Body if LLM is unavailable.
    /// </summary>
    public bool Dynamic { get; set; }

    /// <summary>
    ///     Hints for LLM-powered dynamic response generation.
    ///     Describes what the response should look like so the LLM can generate realistic content.
    /// </summary>
    public PackResponseHints? ResponseHints { get; set; }
}

/// <summary>
///     Hints that guide LLM generation of dynamic honeypot responses.
///     The LLM uses these to produce content that looks realistic for the simulated product.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PackResponseHints
{
    /// <summary>What this endpoint represents (e.g., "WordPress REST API user list", "PHP config file").</summary>
    public string? EndpointDescription { get; set; }

    /// <summary>Expected response format: json, xml, html, plaintext, php.</summary>
    public string? ResponseFormat { get; set; }

    /// <summary>
    ///     Schema or structure hint for the response body.
    ///     For JSON: a sample structure like {"users": [{"id": 1, "name": "..."}]}.
    ///     For HTML: describe the page structure ("WordPress login form with username/password fields").
    ///     For XML: describe the XML schema ("XMLRPC method response").
    /// </summary>
    public string? BodySchema { get; set; }

    /// <summary>Expected HTTP methods that trigger this endpoint (GET, POST, etc.).</summary>
    public List<string>? ExpectedMethods { get; set; }

    /// <summary>
    ///     What a multi-step exploit flow looks like for this endpoint.
    ///     Helps the LLM maintain context across sequential requests from the same bot.
    ///     E.g., "Step 1: POST login form -> Step 2: GET admin dashboard -> Step 3: POST file upload".
    /// </summary>
    public string? ExploitFlow { get; set; }

    /// <summary>
    ///     Product-specific context: framework version, plugins installed, PHP version, etc.
    ///     Fed to the LLM to make generated responses version-accurate.
    /// </summary>
    public Dictionary<string, string>? ProductContext { get; set; }

    /// <summary>
    ///     Error response that should be returned for invalid/unexpected payloads.
    ///     Helps the LLM generate appropriate error responses when the bot sends malformed exploits.
    /// </summary>
    public string? ErrorTemplate { get; set; }
}

/// <summary>
///     A CVE module that defines probe paths associated with a specific vulnerability.
///     When a request matches a CVE probe path, it's a strong indicator of malicious scanning.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PackCveModule
{
    public string CveId { get; set; } = "";
    public string? Severity { get; set; }
    public List<string> AffectedVersions { get; set; } = [];
    public List<string> ProbePaths { get; set; } = [];
    public PackResponseTemplate? ProbeResponse { get; set; }
    public string? Description { get; set; }
}

/// <summary>
///     Timing profile for realistic response delays.
/// </summary>
[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class PackTimingProfile
{
    public int MinResponseMs { get; set; } = 50;
    public int MaxResponseMs { get; set; } = 300;
    public int JitterMs { get; set; } = 50;
}
