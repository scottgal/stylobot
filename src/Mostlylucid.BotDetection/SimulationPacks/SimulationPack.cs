namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     A simulation pack defines a fake product installation (e.g., WordPress 5.9)
///     that acts as a honeypot for vulnerability scanners and exploit bots.
///     Includes honeypot paths, CVE probe signatures, and realistic response templates.
/// </summary>
public sealed record SimulationPack
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Framework { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }

    /// <summary>System prompt additions giving the LLM domain vocabulary and API style for this pack.</summary>
    public string? PromptPersonality { get; init; }

    public List<PackHoneypotPath> HoneypotPaths { get; init; } = [];
    public List<PackResponseTemplate> ResponseTemplates { get; init; } = [];
    public List<PackCveModule> CveModules { get; init; } = [];
    public PackTimingProfile TimingProfile { get; init; } = new();
}

/// <summary>
///     A honeypot path within a simulation pack.
///     Glob patterns are matched using FileSystemName.MatchesSimpleExpression.
/// </summary>
public sealed record PackHoneypotPath
{
    /// <summary>Glob pattern to match against request paths.</summary>
    public required string Path { get; init; }

    /// <summary>Confidence delta when this path is matched (0.0-1.0).</summary>
    public double Confidence { get; init; } = 0.9;

    /// <summary>Weight multiplier for the detection contribution.</summary>
    public double Weight { get; init; } = 2.0;

    /// <summary>Category label for grouping (e.g., "wordpress-auth").</summary>
    public string? Category { get; init; }
}

/// <summary>
///     A response template that the SimulationPackResponder or LLM API can serve for matched paths.
///     Static templates serve the Body directly. Dynamic templates provide hints for LLM generation.
/// </summary>
public sealed record PackResponseTemplate
{
    public required string PathPattern { get; init; }
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "text/html";
    public required string Body { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
    public int MinDelayMs { get; init; }
    public int MaxDelayMs { get; init; } = 100;

    /// <summary>
    ///     When true, the Body is a prompt/description for LLM generation rather than static content.
    ///     The LLM API uses the body as context along with ResponseHints to generate dynamic responses.
    ///     Falls back to static Body if LLM is unavailable.
    /// </summary>
    public bool Dynamic { get; init; }

    /// <summary>
    ///     Hints for LLM-powered dynamic response generation.
    ///     Describes what the response should look like so the LLM can generate realistic content.
    /// </summary>
    public PackResponseHints? ResponseHints { get; init; }
}

/// <summary>
///     Hints that guide LLM generation of dynamic honeypot responses.
///     The LLM uses these to produce content that looks realistic for the simulated product.
/// </summary>
public sealed record PackResponseHints
{
    /// <summary>What this endpoint represents (e.g., "WordPress REST API user list", "PHP config file").</summary>
    public string? EndpointDescription { get; init; }

    /// <summary>Expected response format: json, xml, html, plaintext, php.</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>
    ///     Schema or structure hint for the response body.
    ///     For JSON: a sample structure like {"users": [{"id": 1, "name": "..."}]}.
    ///     For HTML: describe the page structure ("WordPress login form with username/password fields").
    ///     For XML: describe the XML schema ("XMLRPC method response").
    /// </summary>
    public string? BodySchema { get; init; }

    /// <summary>Expected HTTP methods that trigger this endpoint (GET, POST, etc.).</summary>
    public List<string>? ExpectedMethods { get; init; }

    /// <summary>
    ///     What a multi-step exploit flow looks like for this endpoint.
    ///     Helps the LLM maintain context across sequential requests from the same bot.
    ///     E.g., "Step 1: POST login form → Step 2: GET admin dashboard → Step 3: POST file upload".
    /// </summary>
    public string? ExploitFlow { get; init; }

    /// <summary>
    ///     Product-specific context: framework version, plugins installed, PHP version, etc.
    ///     Fed to the LLM to make generated responses version-accurate.
    /// </summary>
    public Dictionary<string, string>? ProductContext { get; init; }

    /// <summary>
    ///     Error response that should be returned for invalid/unexpected payloads.
    ///     Helps the LLM generate appropriate error responses when the bot sends malformed exploits.
    /// </summary>
    public string? ErrorTemplate { get; init; }
}

/// <summary>
///     A CVE module that defines probe paths associated with a specific vulnerability.
///     When a request matches a CVE probe path, it's a strong indicator of malicious scanning.
/// </summary>
public sealed record PackCveModule
{
    public required string CveId { get; init; }
    public string? Severity { get; init; }
    public List<string> AffectedVersions { get; init; } = [];
    public required List<string> ProbePaths { get; init; }
    public PackResponseTemplate? ProbeResponse { get; init; }
    public string? Description { get; init; }
}

/// <summary>
///     Timing profile for realistic response delays.
/// </summary>
public sealed record PackTimingProfile
{
    public int MinResponseMs { get; init; } = 50;
    public int MaxResponseMs { get; init; } = 300;
    public int JitterMs { get; init; } = 50;
}
