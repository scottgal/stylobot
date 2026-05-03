using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class AssetHashStoreTests : IAsyncLifetime
{
    private readonly string _dbName = $"asset_hash_test_{Guid.NewGuid():N}";
    private string Cs => $"Data Source={_dbName};Mode=Memory;Cache=Shared";
    private CentroidSequenceStore _centroidStore = null!;
    private AssetHashStore _store = null!;

    public async Task InitializeAsync()
    {
        _centroidStore = new CentroidSequenceStore(Cs, NullLogger<CentroidSequenceStore>.Instance);
        _store = new AssetHashStore(Cs, _centroidStore, NullLogger<AssetHashStore>.Instance);
        await _centroidStore.InitializeAsync();
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    [Fact]
    public async Task RecordHashAsync_first_time_returns_false()
    {
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        Assert.False(changed);
    }

    [Fact]
    public async Task RecordHashAsync_same_hash_returns_false()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        Assert.False(changed);
    }

    [Fact]
    public async Task RecordHashAsync_different_hash_returns_true()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        var changed = await _store.RecordHashAsync("/vendor/tailwind.css", "\"def456\"");
        Assert.True(changed);
    }

    [Fact]
    public async Task RecordHashAsync_change_marks_endpoint_stale()
    {
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"abc123\"");
        await _store.RecordHashAsync("/vendor/tailwind.css", "\"def456\"");
        Assert.True(_centroidStore.IsEndpointStale("/vendor/tailwind.css"));
    }

    [Fact]
    public async Task IsRecentlyChanged_returns_false_when_no_change()
    {
        await _store.RecordHashAsync("/vendor/app.js", "\"hash1\"");
        Assert.False(_store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public async Task IsRecentlyChanged_returns_true_after_change()
    {
        await _store.RecordHashAsync("/vendor/app.js", "\"hash1\"");
        await _store.RecordHashAsync("/vendor/app.js", "\"hash2\"");
        Assert.True(_store.IsRecentlyChanged("/vendor/app.js"));
    }

    [Fact]
    public void IsRecentlyChanged_returns_false_for_unknown_path()
    {
        Assert.False(_store.IsRecentlyChanged("/unknown.css"));
    }
}
