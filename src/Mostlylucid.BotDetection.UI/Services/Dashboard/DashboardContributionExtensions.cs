using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.UI.Services.Dashboard;

public static class DashboardContributionExtensions
{
    /// <summary>
    ///     Registers a pack's contribution. Callers register one per slot they
    ///     contribute to. The registry collects via <c>IEnumerable&lt;IDashboardContribution&gt;</c>.
    /// </summary>
    public static IServiceCollection AddDashboardContribution<T>(this IServiceCollection s)
        where T : class, IDashboardContribution
        => s.AddSingleton<IDashboardContribution, T>();
}
