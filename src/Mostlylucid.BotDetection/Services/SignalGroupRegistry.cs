using System.Collections.Frozen;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class SignalGroupRegistry : ISignalGroupRegistry
{
    private readonly FrozenDictionary<string, IReadOnlyList<string>> _groups;

    public SignalGroupRegistry(IEnumerable<SignalGroupDefinition> definitions)
    {
        _groups = definitions
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .ToFrozenDictionary(
                d => d.Name,
                d => (IReadOnlyList<string>)[..d.Signals],
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Resolve(string groupReference)
    {
        if (!groupReference.StartsWith('$'))
            return [];

        var name = groupReference[1..];
        return _groups.TryGetValue(name, out var signals) ? signals : [];
    }

    public bool TryGetGroup(string groupName, out IReadOnlyList<string> signals)
    {
        if (_groups.TryGetValue(groupName, out var found))
        {
            signals = found;
            return true;
        }
        signals = [];
        return false;
    }
}
