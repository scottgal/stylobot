using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Sidecar.Client;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Sidecar;

public class SidecarMiddlewareFailureTests
{
    [Fact]
    public void Default_OnFailure_IsFailOpen()
    {
        var opts = new SidecarClientOptions();
        Assert.Equal(FailureMode.FailOpen, opts.OnFailure);
    }

    [Fact]
    public void CanSetOnFailure_ToFailClosed()
    {
        var opts = new SidecarClientOptions { OnFailure = FailureMode.FailClosed };
        Assert.Equal(FailureMode.FailClosed, opts.OnFailure);
    }
}
