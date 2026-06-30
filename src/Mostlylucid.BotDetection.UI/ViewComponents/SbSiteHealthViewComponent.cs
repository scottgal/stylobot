using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders two <c>sb-chartlet</c> instances side-by-side on the Traffic
///     page: upstream latency (Line) and error-rate stack (StackedArea), both
///     sourced from <see cref="ISiteHealthQuery"/>. Per
///     <c>feedback_remote_mode_optional_di</c> the view component early-returns
///     a graceful empty state when the query isn't registered (hosts that
///     never opted into the rate-limit / degradation atoms).
/// </summary>
public sealed class SbSiteHealthViewComponent : ViewComponent
{
    private readonly ISiteHealthQuery? _query;

    /// <summary>
    ///     <paramref name="query"/> is optional so the view component degrades
    ///     to an empty state on hosts that never opted into the gateway-local
    ///     ring (e.g. test fixtures, hosts that turned the rate-limit feature
    ///     off entirely).
    /// </summary>
    public SbSiteHealthViewComponent(ISiteHealthQuery? query = null)
    {
        _query = query;
    }

    public async Task<IViewComponentResult> InvokeAsync(string? window = null)
    {
        var win = string.IsNullOrWhiteSpace(window) ? "60m" : window;
        if (_query is null)
        {
            return View("Default", new SiteHealthViewModel(
                Window: win,
                LatencyChart: null,
                ErrorsChart: null,
                IsHealthyEmpty: true));
        }

        var history = await _query.GetHistoryAsync(win, HttpContext.RequestAborted);
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