namespace Mostlylucid.BotDetection.UI.Services.Dashboard;

/// <summary>
///     Resolves the registered <see cref="IDashboardContribution"/> set for a
///     given slot. Built from the DI container's <c>IEnumerable&lt;IDashboardContribution&gt;</c>;
///     packs that aren't loaded contribute nothing.
/// </summary>
public sealed class DashboardContributionRegistry
{
    private readonly IReadOnlyList<IDashboardContribution> _all;

    public DashboardContributionRegistry(IEnumerable<IDashboardContribution> contributions)
        => _all = contributions.OrderBy(c => c.Order).ToList();

    public IReadOnlyList<IDashboardContribution> For(DashboardContributionSlot slot)
        => _all.Where(c => c.Slot == slot).ToList();
}
