using System.Collections.Concurrent;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Direct coverage for the per-IP rate-limit primitive PR #29 added for
///     the bulk-export and diagnostics endpoints. Failure mode if untested:
///     the limiter silently regresses (off-by-one on the boundary, wrong
///     window roll, etc.) and the dashboard becomes an enumeration channel.
/// </summary>
public class DashboardRateLimitTests
{
    [Fact]
    public void CheckRateLimit_AllowsFirstRequest()
    {
        var store = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
        var (allowed, remaining) = StyloBotDashboardMiddleware.CheckRateLimit(
            "203.0.113.1", DateTime.UtcNow, store, limit: 6);
        Assert.True(allowed);
        Assert.Equal(5, remaining);
    }

    [Fact]
    public void CheckRateLimit_AllowsUpToLimit()
    {
        var store = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            var (allowed, _) = StyloBotDashboardMiddleware.CheckRateLimit(
                "203.0.113.1", now, store, limit: 6);
            Assert.True(allowed, $"request {i + 1} of 6 must be allowed");
        }
    }

    [Fact]
    public void CheckRateLimit_BlocksAfterLimit()
    {
        var store = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
            StyloBotDashboardMiddleware.CheckRateLimit("203.0.113.1", now, store, limit: 6);

        var (allowed, remaining) = StyloBotDashboardMiddleware.CheckRateLimit(
            "203.0.113.1", now, store, limit: 6);
        Assert.False(allowed);
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void CheckRateLimit_IsPerIp()
    {
        var store = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
            StyloBotDashboardMiddleware.CheckRateLimit("203.0.113.1", now, store, limit: 6);

        // Second IP gets its own window
        var (allowed, remaining) = StyloBotDashboardMiddleware.CheckRateLimit(
            "203.0.113.2", now, store, limit: 6);
        Assert.True(allowed);
        Assert.Equal(5, remaining);
    }

    [Fact]
    public void CheckRateLimit_ResetsAfterWindow()
    {
        var store = new ConcurrentDictionary<string, (int Count, DateTime WindowStart)>();
        var t0 = DateTime.UtcNow;

        // Burn the window.
        for (var i = 0; i < 6; i++)
            StyloBotDashboardMiddleware.CheckRateLimit("203.0.113.1", t0, store, limit: 6);

        // Past the window (RateLimitWindow internal = 1 minute).
        var (allowed, remaining) = StyloBotDashboardMiddleware.CheckRateLimit(
            "203.0.113.1", t0.AddMinutes(2), store, limit: 6);
        Assert.True(allowed);
        Assert.Equal(5, remaining);
    }
}
