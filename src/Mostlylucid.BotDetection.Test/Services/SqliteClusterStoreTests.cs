using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Pins the write-through contract for the cluster store. The in-memory
///     <see cref="BotClusterService"/> snapshot is a fast lookup; this SQLite store
///     is the source of truth that survives restart.
/// </summary>
public sealed class SqliteClusterStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteClusterStore _store;

    public SqliteClusterStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"clusters-test-{Guid.NewGuid():N}.db");
        _store = new SqliteClusterStore(
            $"Data Source={_dbPath};Cache=Shared",
            NullLogger<SqliteClusterStore>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm");
        if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal");
    }

    private static BotCluster MakeCluster(string id, string? label = null, string? desc = null) => new()
    {
        ClusterId = id,
        Type = BotClusterType.BotProduct,
        MemberSignatures = new List<string> { "sig-a", "sig-b", "sig-c" },
        MemberCount = 3,
        AverageBotProbability = 0.85,
        AverageSimilarity = 0.92,
        Connectedness = 0.75,
        TemporalDensity = 0.6,
        DominantCountry = "DE",
        DominantAsn = "AS12345",
        Label = label,
        Description = desc,
        FirstSeen = DateTimeOffset.UtcNow.AddHours(-2),
        LastSeen = DateTimeOffset.UtcNow,
        DominantIntent = "scraping",
        AverageThreatScore = 0.4
    };

    [Fact]
    public async Task LoadAllAsync_ReturnsEmpty_BeforeFirstWrite()
    {
        var result = await _store.LoadAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ReplaceAllAsync_RoundTripsClusters()
    {
        await _store.ReplaceAllAsync(new[]
        {
            MakeCluster("alpha", "Alpha Label", "Alpha description"),
            MakeCluster("beta", "Beta Label", null)
        });

        var loaded = (await _store.LoadAllAsync()).OrderBy(c => c.ClusterId).ToList();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("alpha", loaded[0].ClusterId);
        Assert.Equal("Alpha Label", loaded[0].Label);
        Assert.Equal("Alpha description", loaded[0].Description);
        Assert.Equal(3, loaded[0].MemberSignatures.Count);
        Assert.Contains("sig-b", loaded[0].MemberSignatures);
        Assert.Equal(BotClusterType.BotProduct, loaded[0].Type);
        Assert.Equal("DE", loaded[0].DominantCountry);
        Assert.Equal("beta", loaded[1].ClusterId);
        Assert.Null(loaded[1].Description);
    }

    [Fact]
    public async Task ReplaceAllAsync_TruncatesPreviousState()
    {
        await _store.ReplaceAllAsync(new[] { MakeCluster("old-1"), MakeCluster("old-2") });
        await _store.ReplaceAllAsync(new[] { MakeCluster("new-1") });

        var loaded = await _store.LoadAllAsync();

        var ids = loaded.Select(c => c.ClusterId).ToHashSet();
        Assert.DoesNotContain("old-1", ids);
        Assert.DoesNotContain("old-2", ids);
        Assert.Contains("new-1", ids);
    }

    [Fact]
    public async Task UpdateLabelAsync_PersistsLlmDerivedLabel()
    {
        await _store.ReplaceAllAsync(new[] { MakeCluster("python-scraper-cluster") });

        await _store.UpdateLabelAsync(
            "python-scraper-cluster",
            "Python scraper swarm",
            "Coordinated Python-requests scrapers hitting /api/data");

        var loaded = await _store.LoadAllAsync();
        var c = Assert.Single(loaded);
        Assert.Equal("Python scraper swarm", c.Label);
        Assert.Equal("Coordinated Python-requests scrapers hitting /api/data", c.Description);
    }

    [Fact]
    public async Task UpdateLabelAsync_PreservesExistingDescription_WhenNullPassed()
    {
        await _store.ReplaceAllAsync(new[] { MakeCluster("c1", "Old Label", "Existing description") });

        await _store.UpdateLabelAsync("c1", "New Label", description: null);

        var loaded = await _store.LoadAllAsync();
        var c = Assert.Single(loaded);
        Assert.Equal("New Label", c.Label);
        Assert.Equal("Existing description", c.Description); // COALESCE keeps prior
    }

    [Fact]
    public async Task UpdateLabelAsync_NoOpForUnknownCluster()
    {
        await _store.ReplaceAllAsync(new[] { MakeCluster("known") });
        await _store.UpdateLabelAsync("does-not-exist", "ignored", "ignored");

        var loaded = await _store.LoadAllAsync();
        Assert.Single(loaded);
        Assert.Equal("known", loaded[0].ClusterId);
    }

    [Fact]
    public async Task ReplaceAllAsync_EmptyCollection_ClearsStore()
    {
        await _store.ReplaceAllAsync(new[] { MakeCluster("temp") });
        await _store.ReplaceAllAsync(Array.Empty<BotCluster>());

        Assert.Empty(await _store.LoadAllAsync());
    }
}
