# Mostlylucid.GeoDetection.Contributor

**Geographic bot detection contributor for Mostlylucid.BotDetection**

This package demonstrates the **contributor plugin pattern** for extending bot detection with domain-specific signals.

## What It Does

- Enriches bot detection with **geographic location data**
- Verifies bot origins (e.g., Googlebot should come from US)
- Detects geo-inconsistencies (e.g., Russian locale from China)
- Flags hosting/datacenter/VPN IPs
- Identifies suspicious countries

## Installation

```bash
dotnet add package Mostlylucid.GeoDetection
dotnet add package Mostlylucid.GeoDetection.Contributor
```

## Usage - It's Really This Simple!

### 1. Register Services

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add core geo-location services
builder.Services.AddGeoLocationServices();

// Add bot detection with orchestrator
builder.Services.AddBotDetection();

// Add geo-detection contributor (that's it!)
builder.Services.AddGeoDetectionContributor(options =>
{
    options.EnableBotVerification = true;  // Verify known bots (Googlebot, etc.)
    options.EnableInconsistencyDetection = true;  // Detect locale/geo mismatches
    options.FlagHostingIps = true;  // Flag datacenter IPs
    options.FlagVpnIps = true;  // Flag VPN/proxy IPs
});
```

### 2. Use Middleware

```csharp
app.UseBotDetection();
```

That's it! The contributor automatically:
- Runs in the first wave alongside IP detection
- Contributes geographic signals to the blackboard
- Integrates with policy evaluation and early-exit logic

## Why This Pattern is Powerful

The GeoDetection integration showcases the **extensibility** of the bot detection system:

### ✅ Zero Configuration Integration
Once registered, contributors work automatically with:
- Wave-based parallel execution
- Quorum-based early exit
- Circuit breaker (if contributors fail repeatedly)
- Policy evaluation

### ✅ Signal-Based Composition
Contributors communicate via **typed signals on the blackboard**:
```csharp
// GeoContributor writes
signals.Add(GeoSignalKeys.GeoCountryCode, "US");

// Other contributors/detectors read
var country = state.GetSignal<string>(GeoSignalKeys.GeoCountryCode);
```

This enables **composable detection** without tight coupling.

### ✅ Simple DI Registration
Just one line:
```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IContributingDetector, GeoContributor>());
```

## Creating Your Own Contributor

Want to add **custom bot detection logic**? Follow this simple pattern:

### 1. Create Your Contributor

```csharp
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

public class MyCustomContributor : ContributingDetectorBase
{
    public override string Name => "MyCustom";
    public override int Priority => 150;  // Run order (lower = earlier)

    // Optional: Only run after certain signals are available
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
        [TriggerCondition.RequireSignal(GeoSignalKeys.GeoCountryCode)];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken)
    {
        // Read signals from blackboard
        var country = state.GetSignal<string>(GeoSignalKeys.GeoCountryCode);
        var userAgent = state.UserAgent;

        // Your custom detection logic
        if (IsSuspiciousPattern(country, userAgent))
        {
            return Single(DetectionContribution.Bot(
                Name, "MyCustomCheck",
                confidenceDelta: 0.6,
                reason: "Custom rule triggered",
                botType: BotType.Unknown,
                weight: 1.5
            ));
        }

        return None();
    }

    private bool IsSuspiciousPattern(string country, string userAgent) => false;
}
```

### 2. Add Extension Method

```csharp
public static class MyCustomContributorExtensions
{
    public static IServiceCollection AddMyCustomContributor(
        this IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IContributingDetector, MyCustomContributor>());
        return services;
    }
}
```

### 3. Register It

```csharp
builder.Services.AddBotDetection();
builder.Services.AddMyCustomContributor();  // Done!
```

Your contributor automatically:
- Runs in the wave-based pipeline
- Can trigger on signals from other contributors
- Contributes to the final bot probability
- Respects early-exit and circuit breaker logic

## Signal Keys - Extensible by Design

Signal keys use `partial class` for extension:

```csharp
// In Mostlylucid.GeoDetection.Contributor
public static partial class GeoSignalKeys
{
    public const string GeoCountryCode = "geo.country_code";
    public const string GeoIsVpn = "geo.is_vpn";
    // ... more geo signals
}

// In your custom package
public static partial class MySignalKeys
{
    public const string MyCustomSignal = "my.custom_signal";
}
```

## Example: Bot Origin Verification

The GeoContributor verifies known bot origins:

```csharp
private static readonly Dictionary<string, string[]> KnownBotOrigins = new()
{
    ["googlebot"] = ["US"],
    ["bingbot"] = ["US"],
    ["yandexbot"] = ["RU", "FI", "NL"],
    ["baiduspider"] = ["CN", "HK"]
};
```

If a User-Agent claims to be "Googlebot" but the IP is from China:
- **Confidence Delta**: +0.8 (high probability of bot)
- **Weight**: 2.0 (strong signal)
- **Bot Type**: Scraper
- **Reason**: "FAKE Googlebot from China"

This catches **bot impersonation** attempts.

## Geographic Signals Reference

| Signal Key | Type | Description |
|------------|------|-------------|
| `geo.country_code` | string | ISO 3166-1 alpha-2 country code |
| `geo.country_name` | string | Full country name |
| `geo.city` | string | City name |
| `geo.timezone` | string | IANA timezone |
| `geo.is_vpn` | bool | IP is from VPN/proxy |
| `geo.is_hosting` | bool | IP is from datacenter/hosting |
| `geo.is_suspicious_country` | bool | Country is in suspicious list |
| `geo.bot_verified` | bool | Known bot verified from expected country |
| `geo.bot_origin_mismatch` | bool | Bot claims identity but wrong country |

## Configuration

```json
{
  "BotDetection": {
    "Geo": {
      "EnableBotVerification": true,
      "EnableInconsistencyDetection": true,
      "FlagHostingIps": true,
      "FlagVpnIps": true,
      "SuspiciousCountries": ["KP", "CN"],
      "TrustedCountries": ["US", "GB", "DE"],
      "VerifiedBotConfidenceBoost": 0.3,
      "BotOriginMismatchPenalty": 0.8,
      "SignalWeight": 1.0,
      "Priority": 100
    }
  }
}
```

## Why GeoDetection is a Separate Package

The contributor pattern allows:
- **Optional dependencies** - Don't need geo if you don't want it
- **Third-party extensions** - Anyone can publish contributors
- **Clean separation** - Core bot detection has no geo coupling

This is the **plugin architecture** in action!

## License

MIT

A bot detection contributor that provides detailed geographic location analysis for request validation. This package bridges `Mostlylucid.GeoDetection` with `Mostlylucid.BotDetection` to enable geo-based bot detection.

## Features

- **Geographic Location Signals**: Provides country, region, city, and coordinates for requests
- **Geo-Inconsistency Detection**: Detects bots claiming to be from one location but IP suggests another
- **Bot Verification**: Validates that claimed bots (e.g., Googlebot) originate from expected geographic regions
- **VPN/Proxy Detection**: Flags requests from known VPN/proxy/hosting providers
- **Timezone Validation**: Detects mismatches between claimed timezone and IP-based location

## Installation

```bash
dotnet add package Mostlylucid.GeoDetection.Contributor
```

## Usage

```csharp
// In Program.cs or Startup.cs
services.AddGeoLocationServices(options =>
{
    options.DefaultProvider = GeoProvider.MaxMind;
    options.GeoLite2DatabasePath = "/path/to/GeoLite2-City.mmdb";
});

services.AddBotDetection(options => { /* ... */ });

// Add the geo contributor
services.AddGeoDetectionContributor(options =>
{
    options.EnableBotVerification = true;
    options.EnableInconsistencyDetection = true;
    options.SuspiciousCountries = ["CN", "RU", "KP"]; // Optional
});
```

## Signals Emitted

The contributor emits the following signals to the blackboard:

| Signal Key | Type | Description |
|------------|------|-------------|
| `geo.country_code` | string | ISO 3166-1 alpha-2 country code |
| `geo.country_name` | string | Full country name |
| `geo.region_code` | string | Region/state code |
| `geo.city` | string | City name |
| `geo.latitude` | double | Latitude coordinate |
| `geo.longitude` | double | Longitude coordinate |
| `geo.timezone` | string | Timezone (e.g., "America/New_York") |
| `geo.is_vpn` | bool | Whether IP is known VPN |
| `geo.is_hosting` | bool | Whether IP is from hosting provider |
| `geo.continent_code` | string | Continent code (e.g., "NA", "EU") |

## Geo-Inconsistency Detection

The contributor detects several types of geo-based inconsistencies:

### Bot Origin Verification

Known search engine bots should originate from specific countries:

| Bot | Expected Countries |
|-----|-------------------|
| Googlebot | US |
| Bingbot | US |
| Yandex | RU |
| Baidu | CN |

If a User-Agent claims to be Googlebot but the IP is from China, this is flagged as highly suspicious.

### Timezone Mismatches

If the Accept-Language header suggests a specific locale (e.g., "en-US") but the IP originates from a different timezone, this is flagged.

### Datacenter + Consumer Locale

Browser User-Agents claiming consumer locales from datacenter IPs are flagged.

## Configuration Options

```csharp
public class GeoContributorOptions
{
    // Enable verification that known bots come from expected countries
    public bool EnableBotVerification { get; set; } = true;

    // Enable geo-inconsistency detection
    public bool EnableInconsistencyDetection { get; set; } = true;

    // Countries to flag as suspicious (higher weight)
    public List<string> SuspiciousCountries { get; set; } = [];

    // Countries to always trust (lower weight)
    public List<string> TrustedCountries { get; set; } = [];

    // Priority for this detector (lower = runs earlier)
    public int Priority { get; set; } = 15;
}
```

## License

MIT
