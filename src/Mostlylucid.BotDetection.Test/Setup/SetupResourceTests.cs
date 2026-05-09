using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class SetupResourceTests
{
    [Fact]
    public void ResourceStatus_Fresh_HasCorrectPresence()
    {
        var status = new ResourceStatus("Bot Lists", "desc", ResourcePresence.Fresh, "/tmp/db", "ok");

        Assert.Equal(ResourcePresence.Fresh, status.Presence);
        Assert.Equal("Bot Lists", status.Name);
    }

    [Fact]
    public void ResourceStatus_Missing_HasCorrectPresence()
    {
        var status = new ResourceStatus("ONNX", "desc", ResourcePresence.Missing, "/tmp/models");

        Assert.Equal(ResourcePresence.Missing, status.Presence);
        Assert.Null(status.Detail);
    }

    [Fact]
    public void ResourceStatus_Stale_HasCorrectPresence()
    {
        var status = new ResourceStatus("GeoIP", "desc", ResourcePresence.Stale, "/tmp/geo", "10 days old");

        Assert.Equal(ResourcePresence.Stale, status.Presence);
        Assert.Equal("10 days old", status.Detail);
    }
}
