using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class BeaconStoreTests : IDisposable
{
    private readonly BeaconStore _store;
    private readonly string _dbPath;

    public BeaconStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.db");
        _store = new BeaconStore($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        _store.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task StoreAndLookup_RoundTrips()
    {
        await _store.StoreAsync("abc12345", "fingerprint-1", "/wp-login.php", "wordpress-foss", TimeSpan.FromHours(24));
        var result = await _store.LookupAsync("abc12345");
        Assert.NotNull(result);
        Assert.Equal("fingerprint-1", result!.Fingerprint);
        Assert.Equal("/wp-login.php", result.Path);
        Assert.Equal("wordpress-foss", result.PackId);
    }

    [Fact]
    public async Task Lookup_MissingCanary_ReturnsNull()
    {
        var result = await _store.LookupAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task Lookup_ExpiredBeacon_ReturnsNull()
    {
        await _store.StoreAsync("expired1", "fp-1", "/path", null, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var result = await _store.LookupAsync("expired1");
        Assert.Null(result);
    }

    [Fact]
    public async Task BatchLookup_ReturnsOnlyMatches()
    {
        await _store.StoreAsync("match001", "fp-1", "/a", null, TimeSpan.FromHours(1));
        await _store.StoreAsync("match002", "fp-2", "/b", null, TimeSpan.FromHours(1));
        var results = await _store.BatchLookupAsync(["match001", "nomatch1", "match002"]);
        Assert.Equal(2, results.Count);
        Assert.Equal("fp-1", results["match001"].Fingerprint);
        Assert.Equal("fp-2", results["match002"].Fingerprint);
    }

    [Fact]
    public async Task Cleanup_RemovesExpiredBeacons()
    {
        await _store.StoreAsync("keep0001", "fp-1", "/a", null, TimeSpan.FromHours(1));
        await _store.StoreAsync("expire01", "fp-2", "/b", null, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        var removed = await _store.CleanupExpiredAsync();
        Assert.True(removed >= 1);
        Assert.NotNull(await _store.LookupAsync("keep0001"));
        Assert.Null(await _store.LookupAsync("expire01"));
    }
}
