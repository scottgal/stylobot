using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed record ReactionPackStatusEntry(
    string PackName,
    int CurrentLevel,
    string? CurrentLevelName,
    string? CurrentPolicy,
    string Scope);

public sealed record ReactionPackTransitionEntry(
    string PackName,
    int FromLevel,
    int ToLevel,
    string TriggeredBy,
    double SignalValue,
    DateTimeOffset OccurredAt);

public sealed record ReactionPackDashboardModel(
    IReadOnlyList<ReactionPackStatusEntry> ActivePacks,
    IReadOnlyList<ReactionPackStatusEntry> InactivePacks,
    IReadOnlyList<ReactionPackTransitionEntry> RecentTransitions);

public sealed class ReactionPackDashboardService(
    ReactionPackContext packContext,
    ReactionPackTransitionStore transitionStore,
    IEnumerable<ReactionPackDefinition> packDefinitions)
{
    private readonly IReadOnlyList<ReactionPackDefinition> _packDefinitions = packDefinitions.ToList();

    public async Task<ReactionPackDashboardModel> GetDashboardModelAsync(CancellationToken ct = default)
    {
        var activeStates = packContext.GetActiveStates();
        var activeNames = activeStates.Select(s => s.PackName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activePacks = activeStates
            .Select(s =>
            {
                var def = _packDefinitions.FirstOrDefault(d =>
                    string.Equals(d.Name, s.PackName, StringComparison.OrdinalIgnoreCase));
                var step = def?.Steps.FirstOrDefault(st => st.Level == s.Level);
                return new ReactionPackStatusEntry(s.PackName, s.Level, step?.Name, s.PolicyName, s.Scope);
            })
            .OrderByDescending(p => p.CurrentLevel)
            .ToList();

        var inactivePacks = _packDefinitions
            .Where(d => d.Enabled && !activeNames.Contains(d.Name))
            .Select(d => new ReactionPackStatusEntry(
                d.Name, 0, null, null,
                d.IsGlobal ? "global" : (d.ScopedEndpoint ?? d.Scope)))
            .ToList();

        var transitions = await transitionStore.GetAllRecentTransitionsAsync(50, ct);
        var recentTransitions = transitions
            .Select(t => new ReactionPackTransitionEntry(
                t.PackName, t.FromLevel, t.ToLevel, t.TriggeredBy, t.SignalValue, t.OccurredAt))
            .ToList();

        return new ReactionPackDashboardModel(activePacks, inactivePacks, recentTransitions);
    }
}
