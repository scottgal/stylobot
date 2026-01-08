# Mostlylucid.GeoDetection

Geographic location detection and routing middleware for ASP.NET Core applications with multiple provider support.

## Features

- **Multiple Providers**: ip-api.com (free, no account), MaxMind GeoLite2 (local database), or simple mock
- **Country-Based Routing**: Allow/block by country code with middleware and attributes
- **Memory Caching**: Fast in-memory caching enabled by default
- **Optional Database Caching**: SQLite/EF Core persistent cache (opt-in)
- **Auto-Updates**: Automatic MaxMind database downloads and updates

## Installation

```bash
dotnet add package Mostlylucid.GeoDetection
```

## Quick Start

### Option 1: ip-api.com (Easiest - No Setup Required)

```csharp
// Uses free ip-api.com API, no account needed
builder.Services.AddGeoRoutingWithIpApi();

app.UseGeoRouting();
```

### Option 2: MaxMind GeoLite2 (Best for Production)

```csharp
builder.Services.AddGeoRouting(
    configureProvider: options =>
    {
        options.Provider = GeoProvider.MaxMindLocal;
        options.AccountId = 123456;  // Free account from maxmind.com
        options.LicenseKey = "your-license-key";
    }
);

app.UseGeoRouting();
```

### Option 3: Simple Mock (Development/Testing)

```csharp
builder.Services.AddGeoRoutingSimple();
```

## Providers

| Provider         | Setup        | Pros                    | Cons                               |
|------------------|--------------|-------------------------|------------------------------------|
| **IpApi**        | None         | Free, no account needed | Rate limited (45/min), online only |
| **MaxMindLocal** | Free account | Fast, offline, accurate | Requires account, ~60MB database   |
| **Simple**       | None         | No dependencies         | Mock data only                     |

### Getting a MaxMind Account (Free)

1. Sign up at [maxmind.com/en/geolite2/signup](https://www.maxmind.com/en/geolite2/signup)
2. Generate a license key in your account
3. Configure AccountId and LicenseKey
4. Database auto-downloads on startup

## Configuration

### appsettings.json

```json
{
  "GeoLite2": {
    "Provider": "IpApi",
    "AccountId": null,
    "LicenseKey": null,
    "DatabasePath": "data/GeoLite2-City.mmdb",
    "EnableAutoUpdate": true,
    "CacheDuration": "01:00:00",
    "FallbackToSimple": true
  }
}
```

### Provider Options

```csharp
builder.Services.AddGeoRouting(
    configureRouting: options =>
    {
        options.Enabled = true;
        options.AllowedCountries = new[] { "US", "CA", "GB" };
        options.BlockedCountries = new[] { "XX" };
        options.AddCountryHeader = true;  // Adds X-Country header
        options.StoreInContext = true;    // Store in HttpContext.Items
        options.EnableTestMode = true;    // Allow header override
    },
    configureProvider: options =>
    {
        options.Provider = GeoProvider.IpApi;
        options.CacheDuration = TimeSpan.FromHours(1);
    }
);
```

## Database Caching (Optional)

By default, only memory caching is used. To persist lookups to a database:

```csharp
// Add SQLite database cache (call BEFORE AddGeoRouting)
builder.Services.AddGeoCacheDatabase("Data Source=data/geocache.db");

builder.Services.AddGeoRouting(
    configureCache: options =>
    {
        options.Enabled = true;
        options.CacheExpiration = TimeSpan.FromDays(30);
    }
);
```

Use any EF Core provider by configuring DbContext yourself:

```csharp
builder.Services.AddDbContext<GeoDbContext>(options =>
    options.UseSqlServer(connectionString));
```

## Country-Based Routing

### Endpoint Extensions

```csharp
app.MapGet("/us-only", () => "US Only Content")
   .RequireCountry("US");

app.MapGet("/eu-content", () => "EU Content")
   .RequireCountry("DE", "FR", "IT", "ES", "NL", "BE");

app.MapGet("/blocked", () => "Not for XX")
   .BlockCountries("XX");
```

### MVC Attributes

```csharp
[GeoRoute(AllowedCountries = new[] { "US", "CA" })]
public class NorthAmericaController : Controller
{
    public IActionResult Index() => View();
}

[GeoRoute(BlockedCountries = new[] { "XX" })]
public IActionResult Restricted() => View();
```

## Access Location Data

```csharp
public class MyController : Controller
{
    private readonly IGeoLocationService _geoService;

    public MyController(IGeoLocationService geoService)
    {
        _geoService = geoService;
    }

    public async Task<IActionResult> Index()
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var location = await _geoService.GetLocationAsync(clientIp!);

        return Ok(new
        {
            location?.CountryCode,
            location?.CountryName,
            location?.City,
            location?.Latitude,
            location?.Longitude,
            location?.TimeZone,
            location?.IsVpn,
            location?.IsHosting
        });
    }
}
```

## GeoLocation Model

```csharp
public class GeoLocation
{
    public string CountryCode { get; set; }     // "US", "GB", etc.
    public string CountryName { get; set; }     // "United States"
    public string? ContinentCode { get; set; }  // "NA", "EU", "AS"
    public string? RegionCode { get; set; }     // State/region
    public string? City { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? TimeZone { get; set; }       // "America/New_York"
    public bool IsVpn { get; set; }             // Known VPN/proxy
    public bool IsHosting { get; set; }         // Datacenter IP
}
```

## Statistics

```csharp
var stats = geoService.GetStatistics();
// stats.TotalLookups, stats.CacheHits, stats.DatabaseLoaded, etc.
```

## Notes

- Supports reverse proxies (X-Forwarded-For, CF-Connecting-IP)
- Country codes follow ISO 3166-1 alpha-2 standard
- ip-api.com is rate limited to 45 requests/minute (free tier)
- MaxMind databases update weekly (auto-update enabled by default)
- Private/reserved IPs return "XX" country code

## License

Unlicense - Public Domain

## Links

- [GitHub Repository](https://github.com/scottgal/mostlylucidweb/tree/main/Mostlylucid.GeoDetection)
- [MaxMind GeoLite2](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data/)
- [ip-api.com](https://ip-api.com/)
