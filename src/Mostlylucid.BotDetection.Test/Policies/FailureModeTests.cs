using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

public class FailureModeTests
{
    [Fact]
    public void Default_IsFailOpen()
    {
        var policy = new DetectionPolicy { Name = "test" };
        Assert.Equal(FailureMode.FailOpen, policy.OnFailure);
    }

    [Fact]
    public void Init_SetsFailureMode()
    {
        var policy = new DetectionPolicy { Name = "test", OnFailure = FailureMode.FailClosed };
        Assert.Equal(FailureMode.FailClosed, policy.OnFailure);
    }

    [Fact]
    public void Enum_HasThreeValues()
    {
        var values = Enum.GetValues<FailureMode>();
        Assert.Equal(3, values.Length);
        Assert.Contains(FailureMode.FailOpen, values);
        Assert.Contains(FailureMode.FailClosed, values);
        Assert.Contains(FailureMode.LogOnly, values);
    }
}
