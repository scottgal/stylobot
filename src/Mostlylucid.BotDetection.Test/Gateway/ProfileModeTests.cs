using Mostlylucid.BotDetection.Policies;
using Stylobot.Gateway.Configuration;
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
