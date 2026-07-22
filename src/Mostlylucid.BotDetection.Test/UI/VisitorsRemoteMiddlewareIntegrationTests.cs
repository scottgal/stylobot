using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
/// Reproduces the deployed viewer boundary: the real dashboard middleware and
/// remote event store call a gateway over HTTP. The compose endpoint is allowed
/// to be empty (the failure mode seen on staging), while the direct remote
/// endpoints contain the same live data.
/// </summary>
public sealed class VisitorsRemoteMiddlewareIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Visitors_route_renders_remote_data_when_compose_bundle_is_empty()
    {
        var gateway = new FakeGatewayHandler();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
        {
            Pull = new DashboardSourcePullOptions
            {
                Type = DashboardSourceType.Rest,
                Url = "http://gateway.test",
                TimeoutSeconds = 2
            }
        });
        builder.Services.AddHttpClient<GatewayApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => gateway);
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        // Remote viewer hosts do not run DetectionBroadcastMiddleware; exercise
        // the dashboard middleware boundary directly as production remote hosts do.
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var response = await _app.GetTestClient().GetAsync("/dashboard/visitors");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("remote-visitor", html);
        Assert.Contains("countries-map-visitors", html);
        // "Representative bot/browser" UA labels were dropped when the Signature
        // Patterns cards moved to YourDetectionModel.Compact (fixes duplicate
        // "Your Detection" chrome + overlapping text in the narrow side-by-side
        // slot); the aria-label on the compact shape is the equivalent proof this
        // partial rendered.
        Assert.Contains("Representative behavioural fingerprint", html);
        Assert.Contains("42", html);
        Assert.Contains("/api/v1/compose-batch", gateway.Paths);
        Assert.Contains("/api/v1/summary", gateway.Paths);
        Assert.Contains("/api/v1/topbots", gateway.Paths);
        Assert.Contains("/api/v1/countries", gateway.Paths);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class FakeGatewayHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            object payload = path switch
            {
                "/api/v1/compose-batch" => new
                {
                    summary = (DashboardSummary?)null,
                    timeBuckets = (List<DashboardTimeSeriesPoint>?)null,
                    botAggregate = new List<DashboardTopBotEntry>(),
                    geo = new List<DashboardCountryStats>(),
                    endpoints = new List<DashboardEndpointStats>()
                },
                "/api/v1/summary" => new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow,
                    TotalRequests = 42,
                    BotRequests = 12,
                    HumanRequests = 30,
                    UncertainRequests = 0,
                    UniqueSignatures = 7,
                    HumanFingerprints = 4,
                    BotFingerprints = 3,
                    RiskBandCounts = new(),
                    TopBotTypes = new(),
                    TopActions = new()
                },
                "/api/v1/topbots" => new List<DashboardTopBotEntry>
                {
                    new()
                    {
                        PrimarySignature = "remote-visitor",
                        BotName = "Remote visitor",
                        HitCount = 6,
                        BotProbability = 0.1,
                        LastSeen = DateTime.UtcNow
                    }
                },
                "/api/v1/countries" => new List<DashboardCountryStats>
                {
                    new() { CountryCode = "GB", TotalCount = 42, BotCount = 12 }
                },
                _ => Array.Empty<object>()
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { data = payload })
            };
            return Task.FromResult(response);
        }
    }
}
