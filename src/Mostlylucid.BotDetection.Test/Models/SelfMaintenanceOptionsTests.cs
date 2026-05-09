using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Models;

public class SelfMaintenanceOptionsTests
{
    [Fact]
    public void Defaults_AreWithinReasonableBounds()
    {
        var opts = new SelfMaintenanceOptions();
        Assert.True(opts.SignatureCacheSize > 0);
        Assert.True(opts.SessionCacheSize > 0);
        Assert.True(opts.IntentCacheSize > 0);
        Assert.True(opts.MarkovCohortSize > 0);
    }

    [Fact]
    public void LowMemoryPreset_SmallerThanDefaults()
    {
        var lo = SelfMaintenanceOptions.LowMemory;
        var def = new SelfMaintenanceOptions();
        Assert.True(lo.SignatureCacheSize < def.SignatureCacheSize);
        Assert.True(lo.SessionCacheSize < def.SessionCacheSize);
        Assert.True(lo.IntentCacheSize < def.IntentCacheSize);
        Assert.True(lo.MarkovCohortSize < def.MarkovCohortSize);
    }

    [Fact]
    public void BotDetectionOptions_HasSelfMaintenanceProperty()
    {
        var opts = new BotDetectionOptions();
        Assert.NotNull(opts.SelfMaintenance);
        Assert.Equal(new SelfMaintenanceOptions().SignatureCacheSize, opts.SelfMaintenance.SignatureCacheSize);
    }
}
