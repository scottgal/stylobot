using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class CentroidSequenceStoreTests : IDisposable
{
    private readonly List<string> _tempDbPaths = new();

    private static SessionTransitionData TypicalHumanSession() =>
        new(RequestState.PageView, new Dictionary<(RequestState, RequestState), int>
        {
            { (RequestState.PageView, RequestState.StaticAsset), 30 },
            { (RequestState.StaticAsset, RequestState.ApiCall), 10 },
            { (RequestState.ApiCall, RequestState.SignalR), 5 }
        });

    private Func<DbConnection> CreateTempDbFactory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"centroid-test-{Guid.NewGuid():N}.db");
        _tempDbPaths.Add(path);
        var connStr = $"Data Source={path}";
        return () => new SqliteConnection(connStr);
    }

    [Fact]
    public async Task RebuildAsync_UsesLoaderResult_WhenLoaderReturnsData()
    {
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(Enumerable.Repeat(TypicalHumanSession(), 5).ToList()));

        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        var cluster = new BotCluster
        {
            ClusterId = "cluster-1",
            Type = BotClusterType.HumanTraffic,
            MemberSignatures = new List<string> { "sig-a", "sig-b" },
            MemberCount = 25
        };

        await store.RebuildAsync(new[] { cluster });
        var chain = store.TryGetCentroidChain("cluster-1", minSampleSize: 20);
        Assert.NotNull(chain);
        Assert.Equal(RequestState.PageView, chain!.ExpectedStates[0]);
        Assert.Equal(RequestState.StaticAsset, chain.ExpectedStates[1]);
        Assert.Equal(RequestState.ApiCall, chain.ExpectedStates[2]);
        Assert.Equal(RequestState.SignalR, chain.ExpectedStates[3]);
    }

    [Fact]
    public async Task RebuildAsync_FallsBackToTemplate_WhenLoaderEmpty()
    {
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(new List<SessionTransitionData>()));
        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        var cluster = new BotCluster
        {
            ClusterId = "cluster-empty",
            Type = BotClusterType.HumanTraffic,
            MemberSignatures = new List<string> { "sig-x" },
            MemberCount = 25
        };

        await store.RebuildAsync(new[] { cluster });
        var chain = store.TryGetCentroidChain("cluster-empty", minSampleSize: 20);
        Assert.NotNull(chain);
        // Template fallback for human cluster starts with StaticAsset.
        Assert.Equal(RequestState.StaticAsset, chain!.ExpectedStates[0]);
    }

    [Fact]
    public async Task RebuildAsync_NoLoader_UsesTemplate()
    {
        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance);
        await store.InitializeAsync();

        var cluster = new BotCluster
        {
            ClusterId = "cluster-no-loader",
            Type = BotClusterType.HumanTraffic,
            MemberSignatures = new List<string> { "sig-y" },
            MemberCount = 25
        };

        await store.RebuildAsync(new[] { cluster });
        var chain = store.TryGetCentroidChain("cluster-no-loader", minSampleSize: 20);
        Assert.NotNull(chain);
        Assert.Equal(RequestState.StaticAsset, chain!.ExpectedStates[0]);
    }

    [Fact]
    public async Task LearnedGlobal_BelowMinSessions_NotReady()
    {
        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance);
        await store.InitializeAsync();
        Assert.False(store.IsGlobalReady, "Fresh store with no sessions must not be ready");
    }

    [Fact]
    public async Task LearnedGlobal_LoaderReturnsEnoughSessions_BecomesReady()
    {
        var humanSession = new SessionTransitionData(
            RequestState.PageView,
            new Dictionary<(RequestState, RequestState), int>
            {
                { (RequestState.PageView, RequestState.StaticAsset), 30 },
                { (RequestState.StaticAsset, RequestState.ApiCall), 10 },
                { (RequestState.ApiCall, RequestState.SignalR), 5 }
            });
        var sessions = Enumerable.Repeat(humanSession, 60).ToList();
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(sessions));

        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        await store.RelearnGlobalAsync(minSessions: 50);
        Assert.True(store.IsGlobalReady);
        Assert.Equal(RequestState.PageView, store.GlobalChain.ExpectedStates[0]);
        Assert.Equal(RequestState.StaticAsset, store.GlobalChain.ExpectedStates[1]);
    }

    [Fact]
    public async Task LearnedGlobal_LoaderReturnsTooFewSessions_StaysNotReady()
    {
        var humanSession = new SessionTransitionData(
            RequestState.PageView,
            new Dictionary<(RequestState, RequestState), int>
            {
                { (RequestState.PageView, RequestState.StaticAsset), 10 }
            });
        var sessions = Enumerable.Repeat(humanSession, 10).ToList();
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(sessions));

        var store = new CentroidSequenceStore(
            CreateTempDbFactory(),
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store.InitializeAsync();

        await store.RelearnGlobalAsync(minSessions: 50);
        Assert.False(store.IsGlobalReady, "10 sessions < min 50, not ready");
    }

    [Fact]
    public async Task LearnedGlobal_PersistedAcrossInitialize()
    {
        var humanSession = new SessionTransitionData(
            RequestState.PageView,
            new Dictionary<(RequestState, RequestState), int>
            {
                { (RequestState.PageView, RequestState.StaticAsset), 30 },
                { (RequestState.StaticAsset, RequestState.ApiCall), 10 }
            });
        var sessions = Enumerable.Repeat(humanSession, 60).ToList();
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(sessions));

        var factory = CreateTempDbFactory();
        var store1 = new CentroidSequenceStore(
            factory,
            NullLogger<CentroidSequenceStore>.Instance,
            loader);
        await store1.InitializeAsync();
        await store1.RelearnGlobalAsync(minSessions: 50);

        // Reopen the same DB without a loader; the persisted global should be restored.
        var store2 = new CentroidSequenceStore(
            factory,
            NullLogger<CentroidSequenceStore>.Instance);
        await store2.InitializeAsync();
        Assert.True(store2.IsGlobalReady, "Reopened store must restore IsGlobalReady from persisted row");
        Assert.Equal(RequestState.PageView, store2.GlobalChain.ExpectedStates[0]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _tempDbPaths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var fullPath = path + suffix;
                try
                {
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                }
                catch
                {
                    // best-effort cleanup; tests shouldn't fail because of leftover temp files
                }
            }
        }
    }
}
