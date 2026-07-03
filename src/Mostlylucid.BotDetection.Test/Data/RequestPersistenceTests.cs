using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Services;
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
        store.Setup(s => s.AddRequestBatchAsync(It.IsAny<RequestScope>(), It.IsAny<IReadOnlyList<PersistedRequest>>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);

        var svc = CreateService(store);

        for (var i = 0; i < 20; i++)
            await svc.EnqueueAsync("sig1", "/", "ApiCall", 200, 0.95, 0.9, "High", 1.5, DateTime.UtcNow);

        await svc.DisposeAsync(); // drain all pending writes before asserting
        store.Verify(s => s.AddRequestBatchAsync(
            It.IsAny<RequestScope>(),
            It.Is<IReadOnlyList<PersistedRequest>>(list => list.Any(r => r.BotProbability == 0.95)),
            It.IsAny<CancellationToken>()), Times.AtLeast(1));
    }

    [Fact]
    public async Task LowRiskRequest_WrittenUnderNormalLoad()
    {
        var writtenCount = 0;
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.AddRequestBatchAsync(It.IsAny<RequestScope>(), It.IsAny<IReadOnlyList<PersistedRequest>>(), It.IsAny<CancellationToken>()))
             .Callback<RequestScope, IReadOnlyList<PersistedRequest>, CancellationToken>((_, b, _) => writtenCount += b.Count)
             .Returns(Task.CompletedTask);

        var svc = CreateService(store);

        for (var i = 0; i < 10; i++)
            await svc.EnqueueAsync("sig-human", "/about", "PageView", 200, 0.1, 0.8, "Low", 2.0, DateTime.UtcNow);

        await svc.DisposeAsync(); // drain all pending writes before asserting
        Assert.Equal(10, writtenCount);
    }
}

public class SessionAtomizerTests
{
    [Fact]
    public void SplitIntoSessionGroups_SplitsOn30MinGap()
    {
        var now = DateTime.UtcNow;
        var requests = new List<PersistedRequest>
        {
            MakeReq(now.AddMinutes(-70), "sig"),
            MakeReq(now.AddMinutes(-65), "sig"),
            MakeReq(now.AddMinutes(-60), "sig"),
            MakeReq(now.AddMinutes(-28), "sig"), // 32-min gap -> new session
            MakeReq(now.AddMinutes(-20), "sig"),
            MakeReq(now.AddMinutes(-10), "sig"),
        };
        var ordered = requests.OrderBy(r => r.Timestamp).ToList();

        var groups = SessionAtomizerService.SplitIntoSessionGroups(
            ordered, now, forceFlush: true,
            sessionGap: TimeSpan.FromMinutes(30),
            graceAge: TimeSpan.FromMinutes(35));

        Assert.Equal(2, groups.Count(g => g.Count >= 3));
    }

    private static PersistedRequest MakeReq(DateTime ts, string sig) => new()
    {
        Signature      = sig,
        Timestamp      = ts,
        Path           = "/",
        MarkovState    = "PageView",
        StatusCode     = 200,
        BotProbability = 0.1,
        Confidence     = 0.8,
        RiskBand       = "Low",
        ProcessingMs   = 1.5,
    };
}
