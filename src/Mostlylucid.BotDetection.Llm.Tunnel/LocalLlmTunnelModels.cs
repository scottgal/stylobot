using System.Text.Json;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

// The JSON payload encoded inside the sb_llmtunnel_v1_... connection key
public sealed class LlmTunnelConnectionPayload
{
    public int Version { get; set; } = 1;
    public required string NodeId { get; set; }       // "llmn_01hw..."
    public required string NodeName { get; set; }
    public required string TunnelKind { get; set; }   // "cloudflare-quick" or "cloudflare-named"
    public required string TunnelUrl { get; set; }
    public required string AgentPublicKey { get; set; } // base64url, for future ECDH
    public required string ControllerSharedSecret { get; set; } // base64url, for request signing
    public required string KeyId { get; set; }         // "k_01hw..."
    public required string Provider { get; set; }      // "ollama"
    public List<string> Models { get; set; } = [];
    public bool SupportsStreaming { get; set; }
    public int MaxConcurrency { get; set; } = 2;
    public int MaxContext { get; set; } = 8192;
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

// The opaque connection key (prefix + base64url-encoded payload)
public sealed class LlmTunnelConnectionKey
{
    public const string Prefix = "sb_llmtunnel_v1_";

    public required LlmTunnelConnectionPayload Payload { get; set; }

    public string Encode()
    {
        var json = JsonSerializer.Serialize(Payload, TunnelJsonContext.Default.LlmTunnelConnectionPayload);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Prefix + Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static LlmTunnelConnectionKey Decode(string key)
    {
        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException($"Invalid connection key prefix. Expected '{Prefix}'.");
        var b64 = key[Prefix.Length..];
        // Restore standard base64 padding
        var padded = b64.Replace('-', '+').Replace('_', '/');
        var rem = padded.Length % 4;
        if (rem != 0) padded += new string('=', 4 - rem);
        var bytes = Convert.FromBase64String(padded);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var payload = JsonSerializer.Deserialize(json, TunnelJsonContext.Default.LlmTunnelConnectionPayload)
            ?? throw new FormatException("Failed to deserialize connection key payload.");
        return new LlmTunnelConnectionKey { Payload = payload };
    }
}

// Request to import a connection key into a Stylobot site
public sealed class LlmNodeImportRequest
{
    public required string ConnectionKey { get; set; }
}

// Response after successful import
public sealed class LlmNodeImportResponse
{
    public bool Imported { get; set; }
    public required string NodeId { get; set; }
    public required string Name { get; set; }
    public List<string> Models { get; set; } = [];
    public required string TunnelKind { get; set; }
}

// Runtime registry entry for an imported node
public sealed class LlmNodeDescriptor
{
    public required string NodeId { get; set; }
    public required string Name { get; set; }
    public required string TunnelUrl { get; set; }
    public required string TunnelKind { get; set; }
    public required string Provider { get; set; }
    public List<string> Models { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public DateTime LastSeenAt { get; set; }
    public int QueueDepth { get; set; }
    public int FailureCount { get; set; }
    public required string KeyId { get; set; }
    // The signing secret (kept server-side only, not returned to callers)
    [System.Text.Json.Serialization.JsonIgnore]
    public required string ControllerSharedSecret { get; set; }
    public int MaxConcurrency { get; set; } = 2;
    public int MaxContext { get; set; } = 8192;
    public List<string> AdvertisedModels { get; set; } = [];
}

// Model inventory returned by GET /api/v1/llm-tunnel/models
public sealed class LlmNodeModelInventory
{
    public required string Provider { get; set; }
    public List<LlmNodeModelInfo> Models { get; set; } = [];
}

// Per-model metadata
public sealed class LlmNodeModelInfo
{
    public required string Id { get; set; }
    public string? Family { get; set; }
    public string? ParameterSize { get; set; }
    public string? Quantization { get; set; }
    public int ContextLength { get; set; }
    public bool Allowed { get; set; } = true;
    public bool SupportsStreaming { get; set; } = true;
}

// A signed inference request sent from the remote site to the local agent
public sealed class LlmSignedInferenceRequest
{
    public required string RequestId { get; set; }    // "llmreq_01hw..."
    public required string TenantId { get; set; }
    public required string NodeId { get; set; }
    public required string KeyId { get; set; }
    public required string Nonce { get; set; }        // base64url random bytes
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public required LlmTunnelCompletionRequest Payload { get; set; }
    public required string Signature { get; set; }    // base64url HMAC-SHA256
}

// A signed inference response from the agent
public sealed class LlmSignedInferenceResponse
{
    public required string RequestId { get; set; }
    public required string Model { get; set; }
    public required string Content { get; set; }
    public LlmTunnelUsage? Usage { get; set; }
    public long LatencyMs { get; set; }
    public required string Signature { get; set; }   // base64url HMAC-SHA256
}

// Token usage stats
public sealed class LlmTunnelUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

// Completion request payload (inside the signed request)
public sealed class LlmTunnelCompletionRequest
{
    public required string Model { get; set; }
    public List<LlmTunnelMessage> Messages { get; set; } = [];
    public float Temperature { get; set; } = 0.1f;
    public int MaxTokens { get; set; } = 512;
    public bool Stream { get; set; }
    public int TimeoutMs { get; set; } = 5000;
}

// Chat message
public sealed class LlmTunnelMessage
{
    public required string Role { get; set; }
    public required string Content { get; set; }
}

// Optional encrypted envelope (for future AES-GCM mode)
public sealed class LlmTunnelEnvelope
{
    /// Whether this envelope carries encrypted payload.
    public bool Encrypted { get; set; }

    /// AES-GCM nonce (base64url), present when Encrypted = true.
    public string? EncNonce { get; set; }

    /// AES-GCM ciphertext (base64url), present when Encrypted = true.
    public string? Ciphertext { get; set; }

    /// Authentication tag (base64url), present when Encrypted = true.
    public string? Tag { get; set; }

    /// Plaintext payload (base64url JSON), present when Encrypted = false.
    public string? Plaintext { get; set; }
}

// Health response from GET /api/v1/llm-tunnel/health
public sealed class LlmTunnelHealthResponse
{
    public required string Status { get; set; }       // "ready" | "unready"
    public required string NodeId { get; set; }
    public required string Provider { get; set; }
    public required string Version { get; set; }
    public required string KeyId { get; set; }
    public int QueueDepth { get; set; }
    public int MaxConcurrency { get; set; }
    public DateTime StartedAt { get; set; }
}
