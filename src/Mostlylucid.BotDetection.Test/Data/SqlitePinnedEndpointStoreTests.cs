using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Data;

public class SqlitePinnedEndpointStoreTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqlitePinnedEndpointStore _store;

    public SqlitePinnedEndpointStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqlitePinnedEndpointStore(_conn);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task GetAll_Empty_ReturnsEmptyList()
    {
        var result = await _store.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task Add_NewPin_ReturnsPin()
    {
        var pin = await _store.AddAsync("GET", "/config.php", false, "scanner bait");
        Assert.NotNull(pin);
        Assert.Equal("GET", pin!.Method);
        Assert.Equal("/config.php", pin.Path);
        Assert.False(pin.IsHoneypot);
        Assert.Equal("scanner bait", pin.Note);
        Assert.True(pin.Id > 0);
    }

    [Fact]
    public async Task Add_DuplicatePath_ReturnsExisting()
    {
        var first = await _store.AddAsync("GET", "/wp-login.php", true, null);
        var second = await _store.AddAsync("GET", "/wp-login.php", true, "updated note");
        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task GetAll_AfterAdd_ReturnsPin()
    {
        await _store.AddAsync("ANY", "/honeypot", true, null);
        var all = await _store.GetAllAsync();
        Assert.Single(all);
        Assert.True(all[0].IsHoneypot);
    }

    [Fact]
    public async Task Remove_ExistingPin_ReturnsTrue()
    {
        var pin = await _store.AddAsync("GET", "/admin.php", false, null);
        var removed = await _store.RemoveAsync(pin!.Id);
        Assert.True(removed);
        Assert.Empty(await _store.GetAllAsync());
    }

    [Fact]
    public async Task Remove_NonExistentId_ReturnsFalse()
    {
        var removed = await _store.RemoveAsync(999);
        Assert.False(removed);
    }
}
