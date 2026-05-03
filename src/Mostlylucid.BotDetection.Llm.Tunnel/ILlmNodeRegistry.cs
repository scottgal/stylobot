namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// Registry for imported local LLM tunnel nodes.
/// In FOSS, populated from configuration. In paid product, managed via API.
/// </summary>
public interface ILlmNodeRegistry
{
    /// <summary>Register a node from a decoded connection key payload.</summary>
    void Register(LlmNodeDescriptor descriptor);

    /// <summary>Get a node by its node id. Returns null if not found.</summary>
    LlmNodeDescriptor? Get(string nodeId);

    /// <summary>Get all registered nodes.</summary>
    IReadOnlyList<LlmNodeDescriptor> GetAll();

    /// <summary>Remove a node. Returns true if it was present.</summary>
    bool Remove(string nodeId);

    /// <summary>Replace an existing node (update after re-import).</summary>
    void Replace(LlmNodeDescriptor descriptor);
}
