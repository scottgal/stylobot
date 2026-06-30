using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders two <c>sb-chartlet</c> instances side-by-side on the Traffic
///     page: upstream latency (Line) and error-rate stack (StackedArea), both
///     sourced from <see cref="IDashboardEventStore.GetDegradationHistoryAsync"/>.
///     Per <c>feedback_remote_mode_optional_di</c> the view component
///     early-returns a graceful empty state when the store isn't registered
///     (hosts that pared the dashboard out entirely).
///     <para>
///         Previously read through an <c>ISiteHealthQuery</c> indirection that
///         wrapped an in-memory <c>DegradationHistoryAtom</c> ring. The ring
///         violated <c>feedback_no_inmemory_stores</c> (restart lost the whole
///         window) and is gone. Reading the dashboard event store directly
///         removes a parallel data path that previously diverged on remote-mode
///         hosts.
///     </para>
/// </summary>
public sealed class SbSiteHealthViewComponent : ViewComponent
{
    private readonly IDashboardEventStore? _store;

    /// <summary>
    ///     <paramref name="store"/> is optional so the view component degrades
    ///     to an empty state on hosts that never opted into the dashboard.
    /// </summary>
    public SbSiteHealthViewComponent(IDashboardEventStore? store = null)
    {
        _store = store;
    }

    public async Task<IViewComponentResult> InvokeAsync(string? window = null)
    {
        var win = string.IsNullOrWhiteSpace(window) ? "6h" : window;
        if (_store is null)
        {
            return View("Default", new SiteHealthViewModel(
                Window: win,
                LatencyChart: null,
                ErrorsChart: null,
                IsHealthyEmpty: true));
        }

        var span = ParseWindow(win);
        var now = DateTime.UtcNow;
        var history = await _store.GetDegradationHistoryAsync(
            now - span, now, HttpContext.RequestAborted);
        if (history.Count == 0 || SiteHealthChartletBuilder.IsAllHealthy(history))
        {
            return View("Default", new SiteHealthViewModel(
                Window: win,
                LatencyChart: null,
                ErrorsChart: null,
                IsHealthyEmpty: true));
        }

        var latency = SiteHealthChartletBuilder.BuildLatency(history, win);
        var errors = SiteHealthChartletBuilder.BuildErrors(history, win);
        return View("Default", new SiteHealthViewModel(
            Window: win,
            LatencyChart: latency,
            ErrorsChart: errors,
            IsHealthyEmpty: false));
    }

    /// <summary>
    ///     Maps the dashboard's canonical window tokens onto a
    ///     <see cref="TimeSpan"/>. Mirrors
    ///     <c>SiteHealthHistoryEndpoint.ParseWindow</c> so the local
    ///     resolution and the gateway endpoint interpret the token the same
    ///     way (UX1: 6h / 12h / 24h are the new defaults).
    /// </summary>
    private static TimeSpan ParseWindow(string window) => window switch
    {
        "15m"            => TimeSpan.FromMinutes(15),
        "1h" or "60m"    => TimeSpan.FromHours(1),
        "6h"             => TimeSpan.FromHours(6),
        "12h"            => TimeSpan.FromHours(12),
        "24h" or "1d"    => TimeSpan.FromHours(24),
        "7d"             => TimeSpan.FromDays(7),
        _                => TimeSpan.FromHours(6),
    };
}

/// <summary>
///     Backing model for <c>Views/Shared/Components/SbSiteHealth/Default.cshtml</c>.
///     Carries either two chartlets (data flowing) or the
///     <see cref="IsHealthyEmpty"/> flag (no history yet, or every snapshot
///     in the window is fully clean).
/// </summary>
public sealed record SiteHealthViewModel(
    string Window,
    Mostlylucid.BotDetection.UI.Models.ChartletViewModel? LatencyChart,
    Mostlylucid.BotDetection.UI.Models.ChartletViewModel? ErrorsChart,
    bool IsHealthyEmpty);