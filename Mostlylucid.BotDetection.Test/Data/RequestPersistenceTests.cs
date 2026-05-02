using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Moq;

namespace Mostlylucid.BotDetection.Test.Data;

public class RequestPersistenceTests
{
    private static RequestPersistenceService CreateService(Mock<ISessionStore> storeMock)
        => new(storeMock.Object, NullLogger<RequestPersistenceService>.Instance);

    [Fact]
    public async Task BotRequest_AlwaysEnqueued()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AddRequestBatchAsync(It.IsAny<IReadOnlyList<PersistedRequest>>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        await using var svc = CreateService(store);

        for (var i = 0; i < 20; i++)
            await svc.EnqueueAsync("sig1", "/", "ApiCall", 200, 0.95, 0.9, "High", 1.5, DateTime.UtcNow);

        await Task.Delay(500); // let coordinator flush
        store.Verify(s => s.AddRequestBatchAsync(
            It.Is<IReadOnlyList<PersistedRequest>>(list => list.Any(r => r.BotProbability == 0.95)),
            It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }

    [Fact]
    public async Task LowRiskRequest_WrittenUnderNormalLoad()
    {
        var writtenCount = 0;
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AddRequestBatchAsync(It.IsAny<IReadOnlyList<PersistedRequest>>(), It.IsAny<CancellationToken>()))
             .Callback<IReadOnlyList<PersistedRequest>, CancellationToken>((b, _) => writtenCount += b.Count)
             .Returns(Task.CompletedTask);

        await using var svc = CreateService(store);

        for (var i = 0; i < 10; i++)
            await svc.EnqueueAsync("sig-human", "/about", "PageView", 200, 0.1, 0.8, "Low", 2.0, DateTime.UtcNow);

        await Task.Delay(500);
        Assert.Equal(10, writtenCount);
    }
}
