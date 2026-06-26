using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class NullEndpointPerfBaselineTests
{
    [Fact]
    public void Null_baseline_returns_zero_for_any_input()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/"));
        Assert.Equal(0.0, baseline.GetExpectedMs("POST", "/api/users"));
        Assert.Equal(0.0, baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}"));
    }

    [Fact]
    public void Null_baseline_tolerates_empty_and_null_inputs()
    {
        var baseline = new NullEndpointPerfBaseline();
        Assert.Equal(0.0, baseline.GetExpectedMs("", ""));
        Assert.Equal(0.0, baseline.GetExpectedMs(null!, null!));
    }
}