using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionPackContext : IReactionPackContext
{
    private sealed record ActivePackState(string PackName, int Level, string PolicyName, string Scope, int Priority);

    private readonly ConcurrentDictionary<string, ActivePackState> _active = new(StringComparer.Ordinal);

    public string? GetOverridePolicy(string endpoint, string? currentPolicy)
    {
        if (_active.IsEmpty)
            return null;

        ActivePackState? best = null;
        foreach (var state in _active.Values)
        {
            if (!Matches(state.Scope, endpoint))
                continue;
            if (best == null
                || state.Priority > best.Priority
                || (state.Priority == best.Priority && state.Level > best.Level))
                best = state;
        }
        return best?.PolicyName;
    }

    public void SetActiveLevel(string packName, int level, string policyName, string scope, int priority = 0)
    {
        _active[packName] = new ActivePackState(packName, level, policyName, scope, priority);
    }

    public void Deactivate(string packName) => _active.TryRemove(packName, out _);

    public IReadOnlyList<(string PackName, int Level, string PolicyName, string Scope)> GetActiveStates() =>
        _active.Values.Select(s => (s.PackName, s.Level, s.PolicyName, s.Scope)).ToList();

    private static bool Matches(string scope, string endpoint)
    {
        if (string.Equals(scope, "global", StringComparison.OrdinalIgnoreCase))
            return true;
        return endpoint.StartsWith(scope, StringComparison.OrdinalIgnoreCase);
    }
}
