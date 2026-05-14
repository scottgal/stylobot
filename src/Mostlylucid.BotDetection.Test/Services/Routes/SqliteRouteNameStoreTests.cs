using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.Test.Services.Routes;

public class SqliteRouteNameStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteRouteNameStore _store;

    public SqliteRouteNameStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteRouteNameStore(_conn, NullLogger<SqliteRouteNameStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SetAndGet_RoundTrips()
    {
        await _store.SetAsync("GET:/users", "List Users", notes: "paginated", updatedBy: "u1");
        var entry = await _store.GetAsync("GET:/users");
        Assert.NotNull(entry);
        Assert.Equal("List Users", entry!.FriendlyName);
        Assert.Equal("paginated", entry.Notes);
        Assert.Equal("u1", entry.UpdatedBy);
    }

    [Fact]
    public async Task Get_Unknown_ReturnsNull()
    {
        var entry = await _store.GetAsync("GET:/nope");
        Assert.Null(entry);
    }

    [Fact]
    public async Task Set_OverridesExisting()
    {
        await _store.SetAsync("GET:/x", "first", null, "u1");
        await _store.SetAsync("GET:/x", "second", null, "u2");
        var entry = await _store.GetAsync("GET:/x");
        Assert.Equal("second", entry!.FriendlyName);
        Assert.Equal("u2", entry.UpdatedBy);
    }

    [Fact]
    public async Task GetAll_ReturnsEveryEntry()
    {
        await _store.SetAsync("GET:/a", "A", null, "u");
        await _store.SetAsync("POST:/b", "B", null, "u");
        var entries = await _store.GetAllAsync();
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.RouteKey == "GET:/a");
        Assert.Contains(entries, e => e.RouteKey == "POST:/b");
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        await _store.SetAsync("GET:/x", "X", null, "u");
        await _store.RemoveAsync("GET:/x");
        Assert.Null(await _store.GetAsync("GET:/x"));
    }

    [Fact]
    public async Task Set_RejectsEmptyName()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.SetAsync("GET:/x", "", null, "u"));
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
