using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Actions;

public class ActionPolicyRegistryMaskPiiTests
{
    [Fact]
    public void Registry_ContainsMaskPiiBuiltIns()
    {
        var registry = new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>());

        var mask = registry.GetPolicy("mask-pii");
        var strip = registry.GetPolicy("strip-pii");

        Assert.NotNull(mask);
        Assert.NotNull(strip);
        Assert.Equal(ActionType.LogOnly, mask!.ActionType);
        Assert.Equal(ActionType.LogOnly, strip!.ActionType);
    }
}
