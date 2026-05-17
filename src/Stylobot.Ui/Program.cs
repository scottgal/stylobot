using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Extensions;

// stylobot-ui: the dashboard-host product. Two modes, picked at startup via
// StyloBot:Source:Pull:Type:
//
//   "rest"  (recommended): pure viewer. Every dashboard read goes over HTTP to a
//           remote stylobot gateway's /api/v1/* (the gateway must be started with
//           --enable-api and a configured X-SB-Api-Key). No local detection
//           pipeline; no local SQLite. Designed to be hosted inside a network
//           with local-only access.
//
//   "local" (legacy / single-host): runs detection on its own incoming traffic and
//           reads from a local SQLite store. Use the all-in-one stylobot-all binary
//           when you want this topology - it's the right tool for that job.
//
// Defaults:
//   - Binds to http://127.0.0.1:5095 (loopback). Reverse-proxy or override via
//     --urls / ASPNETCORE_URLS / Kestrel:Endpoints to expose externally.
//   - AllowUnauthenticatedAccess = false. Set the dashboard option (or wire a
//     RequireAuthorizationPolicy) before exposing.
//   - MonitoringPack OFF (opt-in via StyloBot:Dashboard:MonitoringPack:Enabled).

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"])
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null or "")
{
    builder.WebHost.UseUrls("http://127.0.0.1:5095");
}

// Default to remote: stylobot-ui is intended as a network-hosted viewer. Operators
// who want single-host detection + dashboard should use stylobot-all instead.
var sourceTypeRaw = builder.Configuration["StyloBot:Source:Pull:Type"];
var sourceType = Enum.TryParse<DashboardSourceType>(sourceTypeRaw, ignoreCase: true, out var parsed)
    ? parsed
    : DashboardSourceType.Rest;
var isRemote = sourceType == DashboardSourceType.Rest;

if (isRemote)
{
    // Remote-viewer wiring. Register every Remote* store BEFORE AddStyloBotDashboard
    // so the TryAdd fallbacks in the dashboard helper skip themselves. Then add the
    // dashboard's tag helpers / SignalR / view services on top - they pick up the
    // remote stores transparently via DI.
    builder.Services.AddStyloBotDashboardRemote(builder.Configuration);
    builder.Services.AddStyloBotDashboard(builder.Configuration, dashboard =>
    {
        dashboard.Enabled = true;
        dashboard.BasePath = "/stylobot";
    });
}
else
{
    // Local single-host wiring. Detection pipeline + local SQLite store + dashboard.
    builder.Services.AddStyloBot(
        configureDashboard: dashboard =>
        {
            builder.Configuration.GetSection("StyloBot:Dashboard").Bind(dashboard);
            dashboard.Enabled = true;
            dashboard.BasePath = "/stylobot";
        },
        configureDetection: detection =>
        {
            detection.Identity.Enabled = true;
        });
    builder.Services.AddBotDetectionTelemetry();
    builder.Services.AddStyloBotApi(options =>
    {
        builder.Configuration.GetSection("StyloBot:Api").Bind(options);
    });
}

var app = builder.Build();

app.UseRouting();

if (isRemote)
{
    // Remote mode: dashboard middleware only. No detection middleware (the viewer
    // never produces detections), no response-headers middleware (no detection ran
    // to populate them), no API surface (nothing local to expose).
    app.UseMiddleware<Mostlylucid.BotDetection.UI.Middleware.StyloBotDashboardMiddleware>();
}
else
{
    app.UseStyloBot();
    app.UseStyloBotResponseHeaders();
}

app.UseAuthorization();

PrintDashboardBanner(app, isRemote);

app.Run();

static void PrintDashboardBanner(WebApplication app, bool isRemote)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;
        if (addresses is null) return;
        foreach (var address in addresses)
        {
            var url = address.TrimEnd('/') + "/_stylobot";
            Console.WriteLine();
            Console.WriteLine($"  stylobot-ui ({(isRemote ? "remote" : "local")}-mode)");
            Console.WriteLine($"  Dashboard: {url}");
            Console.WriteLine();
        }
    });
}
