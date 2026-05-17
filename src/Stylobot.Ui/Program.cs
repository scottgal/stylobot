using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Extensions;

// stylobot-ui: the dashboard-host product. Runs detection on its own incoming traffic
// and serves the StyloBot dashboard at /_stylobot. Minimal surface area by design - no
// app code, no MockLLM. For the all-in-one binary that also fronts a YARP reverse
// proxy, use stylobot-all.
//
// Defaults:
//   - Binds to http://127.0.0.1:5095 (loopback only). Reverse-proxy or override
//     via --urls / ASPNETCORE_URLS / Kestrel:Endpoints to expose externally.
//   - AllowUnauthenticatedAccess = false. Set StyloBot:Dashboard:AllowUnauthenticatedAccess
//     = true (or wire RequireAuthorizationPolicy) before exposing publicly.
//   - Identity layer ON so derived names land in the dashboard from the first request.
//   - MonitoringPack OFF (opt-in via StyloBot:Dashboard:MonitoringPack:Enabled).

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"])
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null or "")
{
    builder.WebHost.UseUrls("http://127.0.0.1:5095");
}

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

var app = builder.Build();

app.UseRouting();
app.UseStyloBot();
app.UseStyloBotResponseHeaders();
app.UseAuthorization();

PrintDashboardBanner(app);

app.Run();

static void PrintDashboardBanner(WebApplication app)
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
            Console.WriteLine($"  Dashboard: {url}");
            Console.WriteLine();
        }
    });
}
