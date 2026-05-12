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

    private string CreateTempDbConnStr()
    {
        var path = Path.Combine(Path.GetTempPath(), $"centroid-test-{Guid.NewGuid():N}.db");
        _tempDbPaths.Add(path);
        return $"Data Source={path}";
    }

    [Fact]
    public async Task RebuildAsync_UsesLoaderResult_WhenLoaderReturnsData()
    {
        var loader = new CentroidSequenceStore.ClusterSessionLoader((_, _, _) =>
            Task.FromResult(Enumerable.Repeat(TypicalHumanSession(), 5).ToList()));

        var store = new CentroidSequenceStore(
            CreateTempDbConnStr(),
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
            CreateTempDbConnStr(),
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
            CreateTempDbConnStr(),
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
