using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class SetupServiceTests
{
    private static Mock<ISetupResource> MakeResource(string name, ResourcePresence presence)
    {
        var mock = new Mock<ISetupResource>();
        mock.Setup(r => r.Name).Returns(name);
        mock.Setup(r => r.Description).Returns("desc");
        mock.Setup(r => r.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceStatus(name, "desc", presence, null));
        mock.Setup(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
    }

    [Fact]
    public async Task CheckAllAsync_ReturnsStatusFromAllResources()
    {
        var r1 = MakeResource("BotLists", ResourcePresence.Fresh);
        var r2 = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([r1.Object, r2.Object]);

        var statuses = await sut.CheckAllAsync();

        Assert.Equal(2, statuses.Count);
        Assert.Contains(statuses, s => s.Name == "BotLists" && s.Presence == ResourcePresence.Fresh);
        Assert.Contains(statuses, s => s.Name == "Onnx" && s.Presence == ResourcePresence.Missing);
    }

    [Fact]
    public async Task DownloadMissingAsync_SkipsFreshResources()
    {
        var fresh = MakeResource("BotLists", ResourcePresence.Fresh);
        var missing = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([fresh.Object, missing.Object]);

        await sut.DownloadMissingAsync(progress: null, force: false);

        fresh.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        missing.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadMissingAsync_WithForce_DownloadsAll()
    {
        var fresh = MakeResource("BotLists", ResourcePresence.Fresh);
        var missing = MakeResource("Onnx", ResourcePresence.Missing);
        var sut = new SetupService([fresh.Object, missing.Object]);

        await sut.DownloadMissingAsync(progress: null, force: true);

        fresh.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
        missing.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DownloadMissingAsync_AlsoDownloadsStaleResources()
    {
        var stale = MakeResource("GeoIP", ResourcePresence.Stale);
        var sut = new SetupService([stale.Object]);

        await sut.DownloadMissingAsync(progress: null, force: false);

        stale.Verify(r => r.DownloadAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckAllAsync_EmptyResources_ReturnsEmpty()
    {
        var sut = new SetupService([]);

        var statuses = await sut.CheckAllAsync();

        Assert.Empty(statuses);
    }
}
