using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies;
using Stylobot.Gateway.Configuration;
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
    public void Channel_DropOldest_WhenFull()
    {
        var channel = new ProfileAnalysisChannel(new ProfileModeOptions { ChannelCapacity = 2 });
        channel.TryEnqueue(MakeSnapshot("req-1"));
        channel.TryEnqueue(MakeSnapshot("req-2"));
        channel.TryEnqueue(MakeSnapshot("req-3")); // drops req-1

        Assert.Equal(2, channel.QueueDepth);
        Assert.Equal(3, channel.TotalEnqueued);
        Assert.Equal(1, channel.TotalDropped);
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
