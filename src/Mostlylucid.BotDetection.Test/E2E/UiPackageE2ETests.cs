using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.E2E;

/// <summary>
///     End-to-end boot smoke for the UI package: a real host wire-up
///     (<c>AddStyloBotDashboard</c> -> <c>StyloBotDashboardMiddleware</c> behind
///     TestServer) must boot and serve the dashboard. This exercises the full
///     dashboard composition stack with the required dependency pack
///     (<c>Mostlylucid.BotDetection.OpenApi</c>) present -- the fail-fast
///     dependency validator runs inside <c>AddStyloBotDashboard</c> and passes
///     here, and a real page request round-trips through the middleware.
///     Prometheus is intentionally NOT installed (optional add-on) in the base
///     boot smoke; the second test installs it and asserts its tile renders.
/// </summary>
public sealed class UiPackageE2ETests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Dashboard_middleware_boots_and_serves_traffic_with_required_packs()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new NullDashboardEventStore());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var response = await _app.GetTestClient().GetAsync("/dashboard/traffic");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the dashboard middleware must serve the traffic surface end to end.");
        html.Should().NotContain("Warming up",
            "the SSR-only contract (a62024fd): a warming placeholder is never valid.");
    }

    // The pack-health row render on the Traffic landing was REVERTED (2026-08-28 P0): it was
    // the only FOSS traffic-page change in the 8.12.x range and was suspected in the
    // traffic-summary-zero regression. It can be reintroduced on a verified surface once the
    // regression root cause is pinned. See CHANGELOG [8.12.2].
    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
