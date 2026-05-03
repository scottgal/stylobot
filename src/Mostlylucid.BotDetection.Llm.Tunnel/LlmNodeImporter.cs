namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>Imports connection keys into node descriptors.</summary>
public static class LlmNodeImporter
{
    public static (LlmNodeDescriptor descriptor, LlmNodeImportResponse response)
        ImportKey(string connectionKeyStr)
    {
        LlmTunnelConnectionKey key;
        try
        {
            key = LlmTunnelConnectionKey.Decode(connectionKeyStr);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Failed to decode connection key: {ex.Message}", ex);
        }

        var p = key.Payload;
        var descriptor = new LlmNodeDescriptor
        {
            NodeId = p.NodeId,
            Name = p.NodeName,
            TunnelUrl = p.TunnelUrl,
            TunnelKind = p.TunnelKind,
            Provider = p.Provider,
            Models = p.Models,
            AdvertisedModels = p.Models,
            KeyId = p.KeyId,
            ControllerSharedSecret = p.ControllerSharedSecret,
            MaxConcurrency = p.MaxConcurrency,
            MaxContext = p.MaxContext,
            LastSeenAt = DateTime.UtcNow,
            Enabled = true
        };

        var response = new LlmNodeImportResponse
        {
            Imported = true,
            NodeId = p.NodeId,
            Name = p.NodeName,
            Models = p.Models,
            TunnelKind = p.TunnelKind
        };

        return (descriptor, response);
    }
}
