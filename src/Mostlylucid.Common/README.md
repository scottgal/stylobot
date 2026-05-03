# Mostlylucid.Common

Shared abstractions, base classes, and utilities for Mostlylucid NuGet packages.

## Features

### Caching

Generic caching service abstraction with memory cache implementation:

```csharp
// Register caching service
services.AddMemoryCachingService<MyData>(options =>
{
    options.DefaultExpiration = TimeSpan.FromMinutes(30);
    options.MaxEntries = 5000;
});

// Use in your service
public class MyService
{
    private readonly ICachingService<MyData> _cache;

    public async Task<MyData?> GetDataAsync(string key)
    {
        return await _cache.GetOrAddAsync(key, async () =>
        {
            // Fetch from source if not cached
            return await FetchFromSourceAsync(key);
        });
    }
}
```

### Statistics Tracking

Base interfaces and implementations for service statistics:

```csharp
public class MyService : IServiceStatistics
{
    private readonly ServiceStatisticsTracker _stats = new();

    public async Task<Result> ProcessAsync(Request request)
    {
        _stats.IncrementRequests();

        var cached = GetFromCache(request);
        if (cached != null)
        {
            _stats.IncrementCacheHit();
            return cached;
        }

        // Process...
    }

    public long TotalRequests => _stats.TotalRequests;
    public long CacheHits => _stats.CacheHits;
    public double CacheHitRate => _stats.CacheHitRate;
}
```

### Periodic Update Service

Base class for background services that periodically update data:

```csharp
public class MyUpdateService : PeriodicUpdateService
{
    protected override TimeSpan UpdateInterval => TimeSpan.FromHours(24);

    protected override Task<DateTime?> GetLastUpdateTimeAsync(CancellationToken ct)
    {
        return Task.FromResult(File.GetLastWriteTimeUtc("data.db") as DateTime?);
    }

    protected override async Task PerformUpdateAsync(CancellationToken ct)
    {
        await DownloadLatestDataAsync(ct);
    }
}
```

### Configuration Interfaces

Standard options interfaces for consistent configuration:

```csharp
public class MyOptions : ICacheableOptions, ITestableOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);
    public int MaxCacheEntries { get; set; } = 10000;
    public bool EnableTestMode { get; set; } = false;
    public string TestModeHeader => "X-Test-Mode";
}
```

### Middleware Base

Base class for middleware with test mode support:

```csharp
public class MyMiddleware : TestModeMiddlewareBase<MyResult>
{
    protected override string ResultKey => "MyResult";
    protected override string TestModeHeader => "X-Test-Mode";
    protected override bool IsTestModeEnabled => _options.EnableTestMode;

    protected override MyResult? CreateTestModeResult(string testValue)
    {
        return new MyResult { TestValue = testValue };
    }

    protected override async Task<MyResult?> ProcessRequestAsync(HttpContext context)
    {
        var ip = context.GetClientIpAddress();
        return await _service.ProcessAsync(ip);
    }
}
```

### IP Address Extraction

Helper for getting client IP addresses with proxy/CDN support:

```csharp
// Automatically checks CF-Connecting-IP, X-Forwarded-For, X-Real-IP, etc.
var clientIp = context.GetClientIpAddress();
```

### Entity Interfaces

Standard interfaces for database entities:

```csharp
public class CachedItem : ICachedEntity
{
    public string Key { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}
```

### OpenTelemetry Support

Base classes and helpers for OpenTelemetry instrumentation across all Mostlylucid packages:

```csharp
using Mostlylucid.Common.Telemetry;

// Get all Mostlylucid activity source names for OpenTelemetry configuration
var sources = TelemetryExtensions.GetMostlylucidActivitySourceNames();
foreach (var source in sources)
{
    tracing.AddSource(source);
}

// Activity source names are available as constants
// ActivitySources.BotDetection = "Mostlylucid.BotDetection"
// ActivitySources.GeoDetection = "Mostlylucid.GeoDetection"
// etc.
```

Available telemetry utilities:

- `TelemetryActivitySource` - Wrapper for System.Diagnostics.ActivitySource
- `TelemetryOptions` - Configuration for telemetry behavior
- `TelemetryConstants` - Standard attribute names following OpenTelemetry semantic conventions
- `TelemetryExtensions` - Helper methods for service registration
- `ActivityExtensions` - Extension methods for recording results and exceptions

## Installation

```bash
dotnet add package Mostlylucid.Common
```

## License

Unlicense (Public Domain)
