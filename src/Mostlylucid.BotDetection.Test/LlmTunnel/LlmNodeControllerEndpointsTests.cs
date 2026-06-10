using Mostlylucid.BotDetection.Llm.Tunnel;
using Xunit;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

public class LlmNodeControllerEndpointsTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static string MakeValidConnectionKey(
        string nodeId = "llmn_test",
        string nodeName = "test-node",
        string tunnelKind = "cloudflare-quick",
        string tunnelUrl = "https://test.trycloudflare.com")
    {
        var payload = new LlmTunnelConnectionPayload
        {
            NodeId = nodeId,
            NodeName = nodeName,
            TunnelKind = tunnelKind,
            TunnelUrl = tunnelUrl,
            AgentPublicKey = "AAAA",
            ControllerSharedSecret = LocalLlmTunnelCrypto.ToBase64Url(
                LocalLlmTunnelCrypto.GenerateSigningSecret()),
            KeyId = "k_test",
            Provider = "ollama",
            Models = ["llama3.2:3b", "gemma2:9b"],
            CreatedAt = DateTime.UtcNow
        };
        var key = new LlmTunnelConnectionKey { Payload = payload };
        return key.Encode();
    }

    // ── LlmNodeImporter tests ──────────────────────────────────────────────

    [Fact]
    public void ImportKey_ValidKey_ReturnsDescriptorAndResponse()
    {
        var keyStr = MakeValidConnectionKey();

        var (descriptor, response) = LlmNodeImporter.ImportKey(keyStr);

        Assert.Equal("llmn_test", descriptor.NodeId);
        Assert.Equal("test-node", descriptor.Name);
        Assert.Equal("https://test.trycloudflare.com", descriptor.TunnelUrl);
        Assert.Equal("cloudflare-quick", descriptor.TunnelKind);
        Assert.Equal("ollama", descriptor.Provider);
        Assert.Equal(2, descriptor.Models.Count);
        Assert.True(descriptor.Enabled);

        Assert.True(response.Imported);
        Assert.Equal("llmn_test", response.NodeId);
        Assert.Equal("test-node", response.Name);
        Assert.Equal("cloudflare-quick", response.TunnelKind);
        Assert.Equal(2, response.Models.Count);
    }

    [Fact]
    public void ImportKey_InvalidPrefix_ThrowsFormatException()
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmNodeImporter.ImportKey("bad_prefix_notakey"));

        Assert.Contains("Failed to decode connection key", ex.Message);
    }

    [Fact]
    public void ImportKey_GarbageBase64_ThrowsFormatException()
    {
        // Correct prefix but garbage base64
        var ex = Assert.Throws<FormatException>(() =>
            LlmNodeImporter.ImportKey("sb_llmtunnel_v1_!!!notbase64!!!"));

        Assert.Contains("Failed to decode connection key", ex.Message);
    }

    // ── TunnelUrl validation (SSRF guard) ──────────────────────────────────

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")] // cloud metadata endpoint
    [InlineData("http://169.254.0.1:8080")]                  // any IPv4 link-local
    [InlineData("http://[fe80::1]:11434")]                   // IPv6 link-local
    public void ImportKey_LinkLocalTunnelUrl_ThrowsFormatException(string tunnelUrl)
    {
        var keyStr = MakeValidConnectionKey(tunnelUrl: tunnelUrl);

        var ex = Assert.Throws<FormatException>(() => LlmNodeImporter.ImportKey(keyStr));

        Assert.Contains("link-local", ex.Message);
    }

    [Theory]
    [InlineData("gopher://internal-service/1")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://10.0.0.1/models")]
    public void ImportKey_NonHttpScheme_ThrowsFormatException(string tunnelUrl)
    {
        var keyStr = MakeValidConnectionKey(tunnelUrl: tunnelUrl);

        var ex = Assert.Throws<FormatException>(() => LlmNodeImporter.ImportKey(keyStr));

        Assert.Contains("http", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void ImportKey_MalformedTunnelUrl_ThrowsFormatException(string tunnelUrl)
    {
        var keyStr = MakeValidConnectionKey(tunnelUrl: tunnelUrl);

        Assert.Throws<FormatException>(() => LlmNodeImporter.ImportKey(keyStr));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]      // loopback sidecar hop
    [InlineData("http://192.168.1.50:11434")]    // LAN GPU box - the primary topology
    [InlineData("https://10.20.30.40")]          // RFC1918
    public void ImportKey_LoopbackAndPrivateTunnelUrl_Allowed(string tunnelUrl)
    {
        var keyStr = MakeValidConnectionKey(tunnelUrl: tunnelUrl);

        var (descriptor, _) = LlmNodeImporter.ImportKey(keyStr);

        Assert.Equal(tunnelUrl, descriptor.TunnelUrl);
    }

    [Fact]
    public void ImportKey_UnresolvableHostname_Allowed()
    {
        // Resolution failure must NOT reject the key: tunnel hostnames are often
        // unresolvable at import time (tunnel not up yet, offline import) and the
        // connection itself fails safely later. Only a POSITIVE link-local
        // resolution rejects.
        var keyStr = MakeValidConnectionKey(
            tunnelUrl: "https://definitely-not-a-real-host.invalid");

        var (descriptor, _) = LlmNodeImporter.ImportKey(keyStr);

        Assert.Equal("https://definitely-not-a-real-host.invalid", descriptor.TunnelUrl);
    }

    // ── InMemoryLlmNodeRegistry tests ──────────────────────────────────────

    [Fact]
    public void Registry_RegisterAndGet_ReturnsDescriptor()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var keyStr = MakeValidConnectionKey();
        var (descriptor, _) = LlmNodeImporter.ImportKey(keyStr);

        registry.Register(descriptor);
        var result = registry.Get("llmn_test");

        Assert.NotNull(result);
        Assert.Equal("llmn_test", result.NodeId);
        Assert.Equal("test-node", result.Name);
    }

    [Fact]
    public void Registry_GetNonExistent_ReturnsNull()
    {
        var registry = new InMemoryLlmNodeRegistry();

        var result = registry.Get("llmn_doesnotexist");

        Assert.Null(result);
    }

    [Fact]
    public void Registry_GetAll_ReturnsAllRegistered()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var (d1, _) = LlmNodeImporter.ImportKey(MakeValidConnectionKey("llmn_a", "node-a"));
        var (d2, _) = LlmNodeImporter.ImportKey(MakeValidConnectionKey("llmn_b", "node-b"));

        registry.Register(d1);
        registry.Register(d2);

        var all = registry.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, n => n.NodeId == "llmn_a");
        Assert.Contains(all, n => n.NodeId == "llmn_b");
    }

    [Fact]
    public void Registry_Remove_ExistingNode_ReturnsTrue()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var (descriptor, _) = LlmNodeImporter.ImportKey(MakeValidConnectionKey());
        registry.Register(descriptor);

        var removed = registry.Remove("llmn_test");

        Assert.True(removed);
        Assert.Null(registry.Get("llmn_test"));
    }

    [Fact]
    public void Registry_Remove_NonExistentNode_ReturnsFalse()
    {
        var registry = new InMemoryLlmNodeRegistry();

        var removed = registry.Remove("llmn_ghost");

        Assert.False(removed);
    }

    [Fact]
    public void Registry_Replace_UpdatesExistingEntry()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var (original, _) = LlmNodeImporter.ImportKey(MakeValidConnectionKey());
        registry.Register(original);

        // Simulate re-import with updated failure count
        var updated = new LlmNodeDescriptor
        {
            NodeId = original.NodeId,
            Name = "updated-name",
            TunnelUrl = original.TunnelUrl,
            TunnelKind = original.TunnelKind,
            Provider = original.Provider,
            Models = original.Models,
            AdvertisedModels = original.AdvertisedModels,
            KeyId = original.KeyId,
            ControllerSharedSecret = original.ControllerSharedSecret,
            LastSeenAt = DateTime.UtcNow,
            Enabled = true,
            FailureCount = 0
        };
        registry.Replace(updated);

        var result = registry.Get("llmn_test");
        Assert.NotNull(result);
        Assert.Equal("updated-name", result.Name);
    }

    [Fact]
    public void Registry_ImportKeyAndRegister_RoundTrip()
    {
        // End-to-end: encode a key, decode it, register it, retrieve it
        var keyStr = MakeValidConnectionKey(
            nodeId: "llmn_roundtrip",
            nodeName: "roundtrip-node",
            tunnelKind: "cloudflare-named",
            tunnelUrl: "https://named.example.com");

        var registry = new InMemoryLlmNodeRegistry();
        var (descriptor, response) = LlmNodeImporter.ImportKey(keyStr);
        registry.Register(descriptor);

        var retrieved = registry.Get("llmn_roundtrip");
        Assert.NotNull(retrieved);
        Assert.Equal("cloudflare-named", retrieved.TunnelKind);
        Assert.Equal("https://named.example.com", retrieved.TunnelUrl);
        Assert.Equal("roundtrip-node", retrieved.Name);

        // Response should reflect what was in the payload
        Assert.True(response.Imported);
        Assert.Equal("llmn_roundtrip", response.NodeId);
    }
}
