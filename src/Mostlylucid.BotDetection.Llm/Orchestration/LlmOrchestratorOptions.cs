namespace Mostlylucid.BotDetection.Llm.Orchestration;

/// <summary>
///     Configuration for the LLM orchestrator: fallback chains, budgets, and per-use-case routing.
///     Configure in appsettings.json under BotDetection:AiDetection or via YAML.
/// </summary>
public sealed class LlmOrchestratorOptions
{
    /// <summary>
    ///     Fallback chain: if primary provider fails/times out, try these in order.
    ///     Each entry is a provider preset name + optional overrides.
    /// </summary>
    public List<LlmFallbackEntry> Fallback { get; set; } = [];

    /// <summary>Budget controls for cost and rate limiting.</summary>
    public LlmBudgetOptions Budget { get; set; } = new();

    /// <summary>
    ///     Per-use-case provider routing. Keys: Classification, BotNaming,
    ///     IntentAnalysis, ClusterDescription, ScoreNarrative.
    ///     If a route isn't configured, uses the primary provider.
    /// </summary>
    public Dictionary<string, LlmFallbackEntry> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A provider entry in a fallback chain or route.</summary>
public sealed class LlmFallbackEntry
{
    /// <summary>Provider preset name (openai, anthropic, groq, ollama, etc.).</summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>API key (if different from primary).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model override.</summary>
    public string? Model { get; set; }

    /// <summary>Base URL override.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Temperature override.</summary>
    public double? Temperature { get; set; }
}

/// <summary>Budget controls for LLM usage.</summary>
public sealed class LlmBudgetOptions
{
    /// <summary>Max LLM requests per hour (0 = unlimited).</summary>
    public int MaxRequestsPerHour { get; set; }

    /// <summary>Estimated max cost per day in USD (0 = unlimited).</summary>
    public double MaxCostPerDay { get; set; }

    /// <summary>Provider to degrade to when budget is exceeded.</summary>
    public string DegradeTo { get; set; } = "ollama";
}
