using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

// Local-only browser harness: real backend SQLite and API, HTTP viewer, controllable outage.
const string url = "http://127.0.0.1:5078";
const string key = "local-dashboard-harness-key";
var directory = Path.Combine(Path.GetTempPath(), "stylobot-dashboard-harness-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(url).UseStaticWebAssets();
var detection = new BotDetectionOptions { DatabasePath = Path.Combine(directory, "backend.db") };
detection.ApiKeys["harness"] = new ApiKeyConfig { Key = key, Enabled = true };
builder.Services.AddSingleton<IOptions<BotDetectionOptions>>(Options.Create(detection));
builder.Services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy(ApiKeyAuthenticationHandler.SchemeName,
    p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
await using var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, Options.Create(detection));
// Register remote stores first. The viewer owns no database; backend queries below own the only SQLite store.
builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
{
    Pull = new() { Type = DashboardSourceType.Rest, Url = url, ApiKey = key }
});
builder.Services.AddStyloBotDashboard(o =>
{
    o.BasePath = "/ops"; o.UseApiTraffic = true; o.AllowUnauthenticatedAccess = true;
});
builder.Services.AddScoped(_ => new TrafficQueryService(store, Options.Create(detection), TimeProvider.System));
var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
var unavailable = false;
app.Use(async (context, next) =>
{
    if (unavailable && context.Request.Path == "/api/v1/dashboard/traffic")
    {
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { title = "Harness outage" });
        return;
    }
    await next(context);
});
app.UseMiddleware<StyloBotDashboardMiddleware>();
app.MapDashboardTrafficEndpoints();
app.MapStyloBotDashboardPages();
app.MapPost("/harness/outage/{enabled:bool}", (bool enabled) => { unavailable = enabled; return Results.NoContent(); });
foreach (var (domain, probability) in new[] { ("one.test", .99), ("one.test", .1), ("two.test", .99) })
    await store.AddDetectionAsync(new DashboardDetectionEvent
    {
        RequestId = Guid.NewGuid().ToString("N"), PrimarySignature = Guid.NewGuid().ToString("N"),
        Timestamp = DateTime.UtcNow.AddMinutes(-5), Domain = domain, BotProbability = probability,
        Confidence = .99, IsBot = probability >= detection.Classification.BotFloor, Method = "GET", Path = "/example", RiskBand = "Low"
    });
Console.WriteLine($"Dashboard harness: {url}/ops/traffic (three seeded requests)");
try { await app.RunAsync(); }
finally
{
    await app.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}
