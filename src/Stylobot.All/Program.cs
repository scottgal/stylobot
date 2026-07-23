using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Extensions;

// stylobot-all: the all-in-one StyloBot product. YARP reverse-proxy detection in
// front, dashboard at /_stylobot in the same process. Operators get a single
// container that does everything; the trade-off is binary size (no AOT, full
// reflection toolkit). For the gateway-only path use stylobot; for dashboard-only
// use stylobot-ui.

var builder = WebApplication.CreateBuilder(args);

// FOSS hard rule: no runtime options-reload, by any path -- including the
// default appsettings.json/appsettings.{env}.json file-watch reload CreateBuilder
// wires up. A config change needs a process restart.
foreach (var source in builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>())
    source.ReloadOnChange = false;

if (string.IsNullOrEmpty(builder.Configuration["urls"])
    && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null or "")
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

// Detection + dashboard. Identity layer on so derived names show up in the dashboard.
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
        // Explicit persistence default: this single-host binary owns a local SQLite
        // store. AddBotDetection now fails LOUD on a null DatabasePath (it used to
        // fall back silently to an unbounded in-memory SQLite DB and OOM), so give it
        // a writable default while honouring any explicit override.
        detection.DatabasePath ??= System.IO.Path.Combine(
            Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
    });

builder.Services.AddBotDetectionTelemetry();

builder.Services.AddStyloBotApi(options =>
{
    builder.Configuration.GetSection("StyloBot:Api").Bind(options);
});

// YARP reverse proxy. Configured via the standard ReverseProxy section in
// appsettings.json; with no clusters configured the binary still runs and serves
// the dashboard, MapReverseProxy just has nothing to match.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Trust X-Forwarded-* from immediate upstream when running behind another proxy.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseRouting();
app.UseStyloBot();
app.UseStyloBotResponseHeaders();
app.UseAuthorization();

// MapReverseProxy goes LAST: dashboard + API routes have already had a chance to
// match. Anything else proxies upstream per ReverseProxy:Routes config.
app.MapReverseProxy();

PrintBanner(app);

app.Run();

static void PrintBanner(WebApplication app)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var addresses = app.Services.GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;
        if (addresses is null) return;
        foreach (var address in addresses)
        {
            Console.WriteLine();
            Console.WriteLine($"  StyloBot listening on {address}");
            Console.WriteLine($"  Dashboard:  {address.TrimEnd('/')}/_stylobot");
            Console.WriteLine($"  REST API:   {address.TrimEnd('/')}/api/v1");
            Console.WriteLine();
        }
    });
}
