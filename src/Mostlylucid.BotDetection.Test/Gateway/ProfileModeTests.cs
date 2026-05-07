using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Gateway;

public class ProfileModeTests
{
    [Fact]
    public void ProfilePolicy_NeverBlocks()
    {
        var policy = DetectionPolicy.Profile;
        Assert.True(policy.ImmediateBlockThreshold > 1.0);
    }

    [Fact]
    public void ProfilePolicy_OnlyRunsSignatureDetector()
    {
        var policy = DetectionPolicy.Profile;
        Assert.Contains("Signature", policy.FastPathDetectors);
        Assert.Single(policy.FastPathDetectors);
        Assert.Empty(policy.SlowPathDetectors);
        Assert.Empty(policy.AiPathDetectors);
        Assert.False(policy.EscalateToAi);
    }

    [Fact]
    public void ProfilePolicy_HasCorrectName()
    {
        Assert.Equal("profile", DetectionPolicy.Profile.Name);
    }

    [Fact]
    public void ProfileModeOptions_DefaultCapacityIs5000()
    {
        var opts = new ProfileModeOptions();
        Assert.Equal(5000, opts.ChannelCapacity);
        Assert.Equal(2, opts.Concurrency);
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void ProfileModeOptions_DatabasePath_DefaultsToNull()
    {
        var opts = new ProfileModeOptions();
        Assert.Null(opts.DatabasePath);
    }
}

public class ProfileAnalysisChannelTests
{
    [Fact]
    public void Channel_EnqueueAndDequeue_SingleItem()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 10 });
        var snapshot = MakeSnapshot("req-1");

        var enqueued = channel.TryEnqueue(snapshot);

        Assert.True(enqueued);
        Assert.Equal(1, channel.QueueDepth);
        Assert.Equal(1, channel.TotalEnqueued);
    }

    [Fact]
    public async Task Channel_ReadAllAsync_ReturnsEnqueuedItem()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 10 });
        var snapshot = MakeSnapshot("req-1");
        channel.TryEnqueue(snapshot);
        channel.Complete();

        var items = new List<ProfileRequestSnapshot>();
        await foreach (var item in channel.ReadAllAsync(CancellationToken.None))
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("req-1", items[0].RequestId);
    }

    [Fact]
    public async Task Channel_DropOldest_WhenFull()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 2 });
        channel.TryEnqueue(MakeSnapshot("req-1"));
        channel.TryEnqueue(MakeSnapshot("req-2"));
        channel.TryEnqueue(MakeSnapshot("req-3")); // drops req-1

        Assert.Equal(2, channel.QueueDepth);
        Assert.Equal(3, channel.TotalEnqueued);
        Assert.Equal(1, channel.TotalDropped);

        // Verify req-1 was dropped (oldest), req-2 and req-3 remain
        channel.Complete();
        var items = new List<ProfileRequestSnapshot>();
        await foreach (var item in channel.ReadAllAsync(CancellationToken.None))
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.RequestId == "req-1");
        Assert.Contains(items, i => i.RequestId == "req-2");
        Assert.Contains(items, i => i.RequestId == "req-3");
    }

    [Fact]
    public void Snapshot_FromHttpContext_CapturesRequiredFields()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/products";
        ctx.Request.Headers["User-Agent"] = "TestBrowser/1.0";
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.2.3.4");

        var snapshot = ProfileRequestSnapshot.From(ctx);

        Assert.Equal("GET", snapshot.Method);
        Assert.Equal("/api/products", snapshot.Path);
        Assert.Equal("1.2.3.4", snapshot.ClientIp);
        Assert.Equal("TestBrowser/1.0", snapshot.UserAgent);
        Assert.NotNull(snapshot.RequestId);
    }

    private static ProfileRequestSnapshot MakeSnapshot(string id) => new()
    {
        RequestId = id,
        ClientIp = "1.2.3.4",
        UserAgent = "TestAgent/1.0",
        Method = "GET",
        Path = "/test",
        Headers = new Dictionary<string, string[]>(),
        CapturedAt = DateTime.UtcNow,
    };
}

public class ProfileCalibrationStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ProfileCalibrationStore _store;

    public ProfileCalibrationStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"profile_test_{Guid.NewGuid():N}.db");
        _store = new ProfileCalibrationStore(_dbPath);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task Insert_ThenDistribution_CountsCorrectly()
    {
        await _store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = "abc",
            BotProbability = 0.2,
            RiskBand = "Low",
            BotType = null,
            BotName = null,
            TopDetector = null,
            PathPattern = "/home",
        }, CancellationToken.None);

        var dist = await _store.GetScoreDistributionAsync(CancellationToken.None);
        Assert.True(dist.TotalAnalyzed >= 1);
        Assert.True(dist.Buckets.ContainsKey("0.2"));
    }

    [Fact]
    public async Task ThresholdSimulation_IncludesCommonThresholds()
    {
        for (int i = 0; i < 5; i++)
            await _store.InsertAsync(new ProfileCalibrationEntry
            {
                SignatureHash = $"sig{i}", BotProbability = 0.8 + i * 0.01,
                RiskBand = "High", BotType = "Scraper", BotName = null,
                TopDetector = "UserAgent", PathPattern = "/catalog",
            }, CancellationToken.None);

        var sim = await _store.GetThresholdSimulationAsync(CancellationToken.None);
        Assert.NotEmpty(sim);
        Assert.All(sim, row =>
        {
            Assert.True(row.Threshold is >= 0.0 and <= 1.0);
            Assert.True(row.WouldBlock >= 0);
        });
    }

    [Fact]
    public async Task Reset_ClearsAllEntries()
    {
        await _store.InsertAsync(new ProfileCalibrationEntry
        {
            SignatureHash = "x", BotProbability = 0.5, RiskBand = "Medium",
            BotType = null, BotName = null, TopDetector = null, PathPattern = "/",
        }, CancellationToken.None);

        await _store.ResetAsync(CancellationToken.None);
        var dist = await _store.GetScoreDistributionAsync(CancellationToken.None);
        Assert.Equal(0, dist.TotalAnalyzed);
    }

    [Fact]
    public async Task RecommendedThreshold_NullWhenNoData()
    {
        var rec = await _store.GetRecommendedThresholdAsync(CancellationToken.None);
        Assert.Null(rec);
    }
}
