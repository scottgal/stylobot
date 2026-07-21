using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Middleware/page-level regression for the Visitors content-cache envelope.
///     Visitors must request the same window and domain scope as Traffic or it misses
///     the already-populated page bundle and falls onto an empty remote cold read.
/// </summary>
public sealed class VisitorsPageWindowParityTests
{
    [Fact]
    public void Visitors_window_matches_selected_traffic_window_and_domains()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<DashboardLayoutOptions>>(
            Options.Create(new DashboardLayoutOptions { DefaultTimeWindowMinutes = 1440 }));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Request.QueryString = new QueryString("?window=6h&domain=alpha.test&domain=beta.test");

        var window = StyloBotDashboardMiddleware.BuildVisitorsPageWindow(context);

        Assert.Equal("all", window.AudienceFilter);
        Assert.Equal(500, window.TopN);
        Assert.Equal(2, window.Domains!.Count);
        Assert.Contains("alpha.test", window.Domains);
        Assert.Contains("beta.test", window.Domains);
        Assert.InRange((window.EndTime!.Value - window.StartTime!.Value).TotalMinutes, 359.9, 360.1);
        Assert.Equal(5, window.BucketMinutes);
    }

    [Fact]
    public void Visitors_window_uses_layout_default_when_query_omits_period()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<DashboardLayoutOptions>>(
            Options.Create(new DashboardLayoutOptions { DefaultTimeWindowMinutes = 720 }));
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        var window = StyloBotDashboardMiddleware.BuildVisitorsPageWindow(context);

        Assert.InRange((window.EndTime!.Value - window.StartTime!.Value).TotalMinutes, 719.9, 720.1);
        Assert.Equal(10, window.BucketMinutes);
    }
}
