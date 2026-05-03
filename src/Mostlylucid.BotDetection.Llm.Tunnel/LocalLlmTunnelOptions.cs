namespace Mostlylucid.BotDetection.Llm.Tunnel;

public class LocalLlmTunnelOptions
{
    public const string SectionName = "BotDetection:AiDetection:LocalTunnel";

    /// Base path for agent endpoints (local agent side).
    public string AgentBasePath { get; set; } = "/api/v1/llm-tunnel";

    /// Default local provider type. Currently only "ollama" is supported.
    public string Provider { get; set; } = "ollama";

    /// URL of the local Ollama instance.
    public string LocalProviderUrl { get; set; } = "http://127.0.0.1:11434";

    /// Request timeout in milliseconds for inference calls.
    public int RequestTimeoutMs { get; set; } = 5000;

    /// Session TTL in hours. After this period the node entry is considered stale.
    public int SessionTtlHours { get; set; } = 24;

    /// Maximum concurrent inference requests the local agent will handle.
    public int MaxConcurrentRequests { get; set; } = 2;

    /// Maximum context tokens accepted from the controller.
    public int MaxContextTokens { get; set; } = 8192;

    /// Single connection key for FOSS config import.
    public string? ConnectionKey { get; set; }

    /// Multiple connection keys for FOSS config import (multiple nodes).
    public List<string> ConnectionKeys { get; set; } = [];

    /// Default model to prefer when multiple models are available.
    public string? DefaultModel { get; set; }
}
