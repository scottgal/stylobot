using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     End-to-end: a scanning client's history survives a simulated ResponseCoordinator restart
///     when a <see cref="SqliteResponseHistoryStore"/> is wired in. Without the durable tier
///     (constructed with historyStore: null, matching a host that never registers it) the second
///     coordinator instance starts the client at zero -- exactly the CLAUDE.md violation the
///     store fixes. Both are pinned here so a future change can't silently reintroduce it.
/// </summary>
public sealed class ResponseCoordinatorDurabilityTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"coordinator-durability-{Guid.NewGuid():N}.db");

    private static ResponseSignal NotFoundSignal(string clientId, string path) => new()
    {
        RequestId = Guid.NewGuid().ToString(),
        ClientId = clientId,
        Timestamp = DateTimeOffset.UtcNow,
        StatusCode = 404,
        ResponseBytes = 0,
        Path = path,
        Method = "GET",
        BodySummary = new ResponseBodySummary { IsPresent = false, Length = 0 }
    };

    [Fact]
    public async Task RestartWithDurableStore_SeedsPriorScanHistory()
    {
        var options = Options.Create(new BotDetectionOptions());
        var historyStore = new SqliteResponseHistoryStore(
            NullLogger<SqliteResponseHistoryStore>.Instance,
            $"Data Source={_dbPath};Cache=Shared");

        const string clientId = "203.0.113.5:AAAAAAAA";
        await using (var first = new ResponseCoordinator(NullLogger<ResponseCoordinator>.Instance, options, historyStore))
        {
            for (var i = 0; i < 5; i++)
                await first.RecordResponseAsync(NotFoundSignal(clientId, $"/scan-{i}"));

            // Sequential per-client processing (KeyedSequentialAtom) -- give it a moment to drain.
            await WaitForBehaviorAsync(first, clientId, expectedCount404: 5);
        }
        await historyStore.FlushAsync();

        // Simulate a restart: a NEW ResponseCoordinator (fresh hot tier, empty ClientResponseTrackingAtom
        // cache) pointed at the SAME durable store.
        await using var second = new ResponseCoordinator(NullLogger<ResponseCoordinator>.Instance, options, historyStore);
        await second.RecordResponseAsync(NotFoundSignal(clientId, "/scan-after-restart"));
        var behavior = await WaitForBehaviorAsync(second, clientId, expectedCount404: 6);

        Assert.Equal(6, behavior.Count404);
        Assert.True(behavior.UniqueNotFoundPaths >= 6,
            $"expected the pre-restart 5 scan paths plus the post-restart one to survive, got {behavior.UniqueNotFoundPaths}");

        historyStore.Dispose();
    }

    [Fact]
    public async Task RestartWithoutDurableStore_LosesPriorScanHistory()
    {
        // Baseline: no historyStore wired (matches the pre-fix / not-registered case). Proves the
        // durability test above is actually exercising the fix, not a tautology.
        var options = Options.Create(new BotDetectionOptions());
        const string clientId = "203.0.113.9:BBBBBBBB";

        await using (var first = new ResponseCoordinator(NullLogger<ResponseCoordinator>.Instance, options, historyStore: null))
        {
            for (var i = 0; i < 5; i++)
                await first.RecordResponseAsync(NotFoundSignal(clientId, $"/scan-{i}"));
            await WaitForBehaviorAsync(first, clientId, expectedCount404: 5);
        }

        await using var second = new ResponseCoordinator(NullLogger<ResponseCoordinator>.Instance, options, historyStore: null);
        var behavior = await second.GetClientBehaviorAsync(clientId);

        Assert.True(behavior is null || behavior.TotalResponses == 0,
            "without a durable tier, a fresh coordinator has no memory of this client");
    }

    private static async Task<ClientResponseBehavior> WaitForBehaviorAsync(
        ResponseCoordinator coordinator, string clientId, int expectedCount404)
    {
        for (var i = 0; i < 50; i++)
        {
            var behavior = await coordinator.GetClientBehaviorAsync(clientId);
            if (behavior is not null && behavior.Count404 >= expectedCount404) return behavior;
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"{clientId} never reached Count404>={expectedCount404} (sequential processing didn't drain in time)");
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
