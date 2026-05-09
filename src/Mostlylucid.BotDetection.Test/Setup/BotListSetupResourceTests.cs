using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Setup;

namespace Mostlylucid.BotDetection.Test.Setup;

public class BotListSetupResourceTests
{
    private readonly Mock<IBotListDatabase> _database;
    private readonly IOptions<BotDetectionOptions> _options;

    public BotListSetupResourceTests()
    {
        _database = new Mock<IBotListDatabase>();
        _options = Options.Create(new BotDetectionOptions { DatabasePath = "/tmp/test-botdetection.db" });
    }

    private BotListSetupResource CreateSut() => new(_database.Object, _options);

    [Fact]
    public async Task CheckAsync_WhenNeverUpdated_ReturnsMissing()
    {
        _database.Setup(d => d.GetLastUpdateTimeAsync("bot_patterns", It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime?)null);

        var sut = CreateSut();
        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Missing, result.Presence);
        Assert.Equal("Bot Lists", result.Name);
    }

    [Fact]
    public async Task CheckAsync_WhenRecentlyUpdated_ReturnsFresh()
    {
        _database.Setup(d => d.GetLastUpdateTimeAsync("bot_patterns", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow.AddHours(-2));

        var sut = CreateSut();
        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Fresh, result.Presence);
    }

    [Fact]
    public async Task CheckAsync_WhenUpdatedMoreThanOneDayAgo_ReturnsStale()
    {
        _database.Setup(d => d.GetLastUpdateTimeAsync("bot_patterns", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DateTime.UtcNow.AddDays(-2));

        var sut = CreateSut();
        var result = await sut.CheckAsync();

        Assert.Equal(ResourcePresence.Stale, result.Presence);
    }

    [Fact]
    public async Task DownloadAsync_CallsInitializeAndUpdate()
    {
        _database.Setup(d => d.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _database.Setup(d => d.UpdateListsAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sut = CreateSut();
        await sut.DownloadAsync(null);

        _database.Verify(d => d.InitializeAsync(It.IsAny<CancellationToken>()), Times.Once);
        _database.Verify(d => d.UpdateListsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
