using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Sub-change 4: ComputeUserAgentsAsync accumulates BytesOut per UA family.
/// </summary>
public class SqliteUaAggregateTests
{
    [Fact]
    public async Task ComputeUserAgentsAsync_bytes_out_summed_per_family()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-bytes");

        // Chrome: 2 detections with known bytes
        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0",  isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0",  isBot: false, bytes: 2000));
        // curl: 1 detection with bytes
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",     isBot: true,  bytes: 500));

        var broadcaster = MakeBroadcaster(fx.Store);
        var summaries = await broadcaster.ComputeUserAgentsAsync();

        // Families are derived from UA parsing — ExtractBrowserFamily maps "Chrome/118.0" → "Chrome"
        // and "curl/7.88" → "curl". Verify bytes were accumulated correctly.
        var chrome = summaries.FirstOrDefault(s => s.Family.Equals("Chrome", StringComparison.OrdinalIgnoreCase));
        chrome.Should().NotBeNull("Chrome family should be present");
        chrome!.BytesOut.Should().Be(3000);

        var curl = summaries.FirstOrDefault(s => s.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));
        curl.Should().NotBeNull("curl family should be present");
        curl!.BytesOut.Should().Be(500);
    }

    [Fact]
    public async Task ComputeUserAgentsAsync_bytes_out_is_zero_when_response_bytes_null()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-bytes-null");

        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88", isBot: true, bytes: null));

        var broadcaster = MakeBroadcaster(fx.Store);
        var summaries = await broadcaster.ComputeUserAgentsAsync();

        var curl = summaries.FirstOrDefault(s => s.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));
        curl.Should().NotBeNull();
        curl!.BytesOut.Should().Be(0);
    }

    [Fact]
    public async Task ComputeUserAgentsAsync_bytes_out_independent_per_family()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-bytes-split");

        // Two distinct families — bytes must not bleed across
        await fx.Store.AddDetectionAsync(MakeDetection("Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0", isBot: false, bytes: 400));
        await fx.Store.AddDetectionAsync(MakeDetection("Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0", isBot: false, bytes: 600));
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",   isBot: true,  bytes: 9999));

        var broadcaster = MakeBroadcaster(fx.Store);
        var summaries = await broadcaster.ComputeUserAgentsAsync();

        var ff  = summaries.FirstOrDefault(s => s.Family.Equals("Firefox", StringComparison.OrdinalIgnoreCase));
        var curl = summaries.FirstOrDefault(s => s.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));

        ff.Should().NotBeNull();
        ff!.BytesOut.Should().Be(1000);

        curl.Should().NotBeNull();
        curl!.BytesOut.Should().Be(9999);
    }

    [Fact]
    public async Task ComputeUserAgentsAsync_p95_processing_time_computed()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-p95");

        // 19 rows at 10 ms, 1 row at 100 ms — all "curl" family.
        // avg = (19*10 + 100) / 20 = 14.5, max = 100
        // p95 = 14.5 + (100 - 14.5) * 0.9 = 91.45
        for (var i = 0; i < 19; i++)
            await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88", isBot: true, bytes: 0) with { ProcessingTimeMs = 10 });
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88", isBot: true, bytes: 0) with { ProcessingTimeMs = 100 });

        var broadcaster = MakeBroadcaster(fx.Store);
        var summaries = await broadcaster.ComputeUserAgentsAsync();

        var curl = summaries.FirstOrDefault(s => s.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));
        curl.Should().NotBeNull();
        curl!.AvgProcessingTimeMs.Should().BeApproximately(14.5, 0.1);
        curl!.P95ProcessingTimeMs.Should().BeApproximately(91.45, 0.5);
    }

    // --- helpers ---

    private static DashboardDetectionEvent MakeDetection(string userAgent, bool isBot, long? bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.9 : 0.1,
            Confidence       = 0.5,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = "/page",
            StatusCode       = 200,
            ProcessingTimeMs = 5,
            // UserAgentRaw is the DB-persisted column. UserAgent is only set for SignalR broadcasts.
            // The broadcaster's ComputeUserAgentsAsync reads UserAgentRaw via the DB path.
            UserAgentRaw     = userAgent,
            ResponseBytes    = bytes,
        };

    private static DashboardSummaryBroadcaster MakeBroadcaster(SqliteDashboardEventStore store)
    {
        var hubCtxMock = new Mock<IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>>();
        var options    = new StyloBotDashboardOptions();
        var sigCache   = new SignatureAggregateCache(options);
        var aggCache   = new DashboardAggregateCache();
        var services   = new ServiceCollection().BuildServiceProvider();

        return new DashboardSummaryBroadcaster(
            hubCtxMock.Object,
            store,
            aggCache,
            sigCache,
            options,
            services,
            NullLogger<DashboardSummaryBroadcaster>.Instance,
            new DashboardUserAgentAggregator(store));
    }
}
