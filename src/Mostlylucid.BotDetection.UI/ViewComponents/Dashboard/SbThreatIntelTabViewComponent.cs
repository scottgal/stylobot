using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

/// <summary>
///     Threat-intel tab. Read-only in FOSS: shows per-provider status (enabled,
///     last refresh, cache size, quota / breaker for live providers) and the
///     master switch. Operator changes are config-only - no edit surface in the
///     view itself.
/// </summary>
public class SbThreatIntelTabViewComponent : ViewComponent
{
    // Optional: remote-mode dashboard viewer hosts (mostlylucid.stylobot.website)
    // do not register IThreatIntelCoordinator -- detection runs on the gateway
    // and the viewer reads via REST. Constructor-injecting a required dep here
    // makes the whole Threats tab 500 on the viewer. Per
    // [[feedback_remote_mode_optional_di]]: dashboard read paths must treat
    // detection-pipeline services as optional and render a disabled / empty
    // state when absent.
    private readonly IThreatIntelCoordinator? _coordinator;

    public SbThreatIntelTabViewComponent(IThreatIntelCoordinator? coordinator = null)
    {
        _coordinator = coordinator;
    }

    public IViewComponentResult Invoke()
    {
        if (_coordinator is null)
        {
            return View(new ThreatIntelTabModel
            {
                IsEnabled = false,
                Providers = Array.Empty<ProviderStatus>()
            });
        }

        var statuses = _coordinator.Providers
            .Select(p => p.GetStatus())
            .OrderBy(s => s.Mode)
            .ThenBy(s => s.Provider, StringComparer.Ordinal)
            .ToList();
        return View(new ThreatIntelTabModel
        {
            IsEnabled = _coordinator.IsEnabled,
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
