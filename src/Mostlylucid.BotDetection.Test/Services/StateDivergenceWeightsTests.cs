using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class StateDivergenceWeightsTests
{
    [Fact]
    public void Default_StaticAsset_IsLowWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.StaticAsset) <= 0.1,
            "StaticAsset should be near-zero - it's browser noise");
    }

    [Fact]
    public void Default_AuthAttempt_IsHighWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.AuthAttempt) >= 0.5,
            "AuthAttempt should be high - it is a meaningful divergence");
    }

    [Fact]
    public void Default_NotFound_IsHighWeight()
    {
        var w = StateDivergenceWeights.Default;
        Assert.True(w.For(RequestState.NotFound) >= 0.4);
    }

    [Fact]
    public void FromParameters_OverridesDefaults()
    {
        var w = StateDivergenceWeights.FromParameters(
            (state, fallback) => state == RequestState.StaticAsset ? 0.99 : fallback);
        Assert.Equal(0.99, w.For(RequestState.StaticAsset));
        Assert.Equal(StateDivergenceWeights.Default.For(RequestState.ApiCall),
                     w.For(RequestState.ApiCall));
    }
}
