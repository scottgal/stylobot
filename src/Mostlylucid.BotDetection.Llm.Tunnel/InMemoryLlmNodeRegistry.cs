using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

public sealed class InMemoryLlmNodeRegistry : ILlmNodeRegistry
{
    private readonly ConcurrentDictionary<string, LlmNodeDescriptor> _nodes = new();

    public void Register(LlmNodeDescriptor descriptor)
        => _nodes[descriptor.NodeId] = descriptor;

    public LlmNodeDescriptor? Get(string nodeId)
        => _nodes.TryGetValue(nodeId, out var d) ? d : null;

    public IReadOnlyList<LlmNodeDescriptor> GetAll()
        => _nodes.Values.ToList().AsReadOnly();

    public bool Remove(string nodeId)
        => _nodes.TryRemove(nodeId, out _);

    public void Replace(LlmNodeDescriptor descriptor)
        => _nodes[descriptor.NodeId] = descriptor;
}
