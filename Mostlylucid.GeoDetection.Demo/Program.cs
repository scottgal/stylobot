using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure forwarded headers (for proxies, load balancers, Cloudflare, etc.)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Read provider from config (defaults to IpApi for easy setup)
var providerConfig = builder.Configuration.GetValue<string>("GeoLite2:Provider") ?? "IpApi";
var provider = Enum.TryParse<GeoProvider>(providerConfig, out var p) ? p : GeoProvider.IpApi;

// Optional: Enable SQLite database caching (uncomment to enable)
// builder.Services.AddGeoCacheDatabase();

// Add GeoDetection services
builder.Services.AddGeoRouting(
    options =>
    {
        options.Enabled = true;
        options.EnableTestMode = true; // Allow header override for testing
        options.AddCountryHeader = true; // Add X-Country header to responses
        options.StoreInContext = true; // Store GeoLocation in HttpContext.Items
    },
    options =>
    {
        // Configure from appsettings.json
        var config = builder.Configuration.GetSection("GeoLite2");

        options.Provider = provider;

        // MaxMind settings (only needed if using MaxMindLocal provider)
        if (int.TryParse(config["AccountId"], out var accountId))
            options.AccountId = accountId;

        options.LicenseKey = config["LicenseKey"];
        options.DatabasePath = config["DatabasePath"] ?? "data/GeoLite2-City.mmdb";
        options.EnableAutoUpdate = config.GetValue("EnableAutoUpdate", true);
        options.DownloadOnStartup = config.GetValue("DownloadOnStartup", true);
        options.FallbackToSimple = config.GetValue("FallbackToSimple", true);
    }
);

var app = builder.Build();

// Handle forwarded headers (must be first)
app.UseForwardedHeaders();

app.UseStaticFiles();

// Use geo-routing middleware
app.UseGeoRouting();

// API endpoints
app.MapGet("/", () => Results.File("wwwroot/index.html", "text/html"));

// Lookup IP endpoint
app.MapGet("/api/lookup/{ip}", async (string ip, IGeoLocationService geoService) =>
{
    var location = await geoService.GetLocationAsync(ip);
    if (location == null)
        return Results.NotFound(new { error = "Location not found for IP", ip });

    return Results.Ok(new
    {
        ip,
        location.CountryCode,
        location.CountryName,
        location.ContinentCode,
        location.RegionCode,
        location.City,
        location.Latitude,
        location.Longitude,
        location.TimeZone,
        location.IsVpn,
        location.IsHosting
    });
});

// Lookup current visitor IP
app.MapGet("/api/my-location", async (HttpContext context, IGeoLocationService geoService) =>
{
    var ip = GetClientIp(context);
    var location = await geoService.GetLocationAsync(ip);

    return Results.Ok(new
    {
        ip,
        location = location != null
            ? new
            {
                location.CountryCode,
                location.CountryName,
                location.ContinentCode,
                location.RegionCode,
                location.City,
                location.Latitude,
                location.Longitude,
                location.TimeZone,
                location.IsVpn,
                location.IsHosting
            }
            : null
    });
});

// Statistics endpoint
app.MapGet("/api/stats", (IGeoLocationService geoService) =>
{
    var stats = geoService.GetStatistics();
    return Results.Ok(stats);
});

// Batch lookup endpoint
app.MapPost("/api/lookup/batch", async (string[] ips, IGeoLocationService geoService) =>
{
    var results = new List<object>();
    foreach (var ip in ips.Take(100)) // Limit to 100 IPs
    {
        var location = await geoService.GetLocationAsync(ip);
        results.Add(new
        {
            ip,
            location = location != null
                ? new
                {
                    location.CountryCode,
                    location.CountryName,
                    location.City
                }
                : null
        });
    }

    return Results.Ok(results);
});

// Country check endpoint
app.MapGet("/api/is-from/{countryCode}/{ip}", async (string countryCode, string ip, IGeoLocationService geoService) =>
{
    var isFromCountry = await geoService.IsFromCountryAsync(ip, countryCode);
    return Results.Ok(new { ip, countryCode, isFromCountry });
});

// Example geo-restricted endpoints
app.MapGet("/api/us-only", async (HttpContext context, IGeoLocationService geoService) =>
{
    var ip = GetClientIp(context);
    var location = await geoService.GetLocationAsync(ip);

    if (location?.CountryCode != "US")
        return Results.Json(new { error = "This content is only available in the United States" }, statusCode: 451);

    return Results.Ok(new { message = "Welcome, US visitor!", countryCode = location.CountryCode });
}).RequireCountry("US");

app.MapGet("/api/eu-content", async (HttpContext context, IGeoLocationService geoService) =>
{
    var ip = GetClientIp(context);
    var location = await geoService.GetLocationAsync(ip);
    return Results.Ok(new { message = "EU content", yourCountry = location?.CountryCode });
}).RequireCountry("DE", "FR", "IT", "ES", "NL", "BE", "AT", "PL", "SE", "DK", "FI", "IE", "PT", "GR");

app.Run();

static string GetClientIp(HttpContext context)
{
    // Check for forwarded headers (behind proxy/load balancer)
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrEmpty(forwardedFor))
    {
        var ips = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (ips.Length > 0)
            return ips[0].Trim();
    }

    // Check Cloudflare header
    var cfConnectingIp = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();
    if (!string.IsNullOrEmpty(cfConnectingIp))
        return cfConnectingIp;

    // Fall back to remote IP
    return context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
}