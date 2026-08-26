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

    [Fact]
    public async Task Traffic_landing_renders_the_pack_health_row_when_the_prometheus_pack_is_installed()
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
        builder.Services.AddPrometheusPack(opt => opt.Mode = PrometheusPackMode.Local);

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var html = await _app.GetTestClient().GetStringAsync("/dashboard/traffic");

        // The pack-health row was orphaned by the V2 IA Overview deletion (the page it
        // lived on no longer exists) and never rendered, making pack tiles invisible.
        // It now renders at the top of the Traffic landing (the V2 de-facto overview)
        // so the Prometheus meter tile is actually visible to operators.
        html.Should().Contain("sb-pack-health-row",
            "the pack-health row must render on the V2 Traffic landing when a pack contributes tiles.");
        html.Should().Contain("Metrics",
            "the Prometheus meter-health tile must render when AddPrometheusPack is installed.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
