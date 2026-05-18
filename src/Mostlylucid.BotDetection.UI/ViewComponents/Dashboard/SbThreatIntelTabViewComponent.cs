using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

/// <summary>
///     Threat-intel tab. Read-only in FOSS: shows per-provider status (enabled,
///     last refresh, cache size, quota / breaker for live providers) and the
///     master switch. Operator changes are config-only - no edit surface in the
///     view itself.
/// </summary>
public class SbThreatIntelTabViewComponent(IThreatIntelCoordinator coordinator) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var statuses = coordinator.Providers
            .Select(p => p.GetStatus())
            .OrderBy(s => s.Mode)
            .ThenBy(s => s.Provider, StringComparer.Ordinal)
            .ToList();
        return View(new ThreatIntelTabModel
        {
            IsEnabled = coordinator.IsEnabled,
            Providers = statuses
        });
    }
}

public sealed class ThreatIntelTabModel
{
    public required bool IsEnabled { get; init; }
    public required IReadOnlyList<ProviderStatus> Providers { get; init; }

    public int EnabledCount => Providers.Count(p => p.Enabled);
    public int FailedCount => Providers.Count(p => p.LastRefreshFailed);
    public int LiveCount => Providers.Count(p => p.Mode == ThreatIntelMode.Live);
    public int OfflineCount => Providers.Count(p => p.Mode == ThreatIntelMode.Offline);
}
