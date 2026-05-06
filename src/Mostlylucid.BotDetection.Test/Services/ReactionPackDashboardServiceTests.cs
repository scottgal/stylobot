using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackDashboardServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ReactionPackTransitionStore _store;
    private readonly ReactionPackContext _context;

    public ReactionPackDashboardServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE reaction_pack_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pack_name TEXT NOT NULL,
                from_level INTEGER NOT NULL,
                to_level INTEGER NOT NULL,
                triggered_by TEXT NOT NULL,
                signal_value REAL NOT NULL,
                occurred_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _store = new ReactionPackTransitionStore(_conn);
        _context = new ReactionPackContext();
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();

    [Fact]
    public async Task GetDashboardModel_NoPacks_ReturnsEmpty()
    {
        var svc = new ReactionPackDashboardService(_context, _store, []);
        var model = await svc.GetDashboardModelAsync();
        Assert.Empty(model.ActivePacks);
        Assert.Empty(model.InactivePacks);
        Assert.Empty(model.RecentTransitions);
    }

    [Fact]
    public async Task GetDashboardModel_InactivePack_AppearsInInactive()
    {
        var def = new ReactionPackDefinition { Name = "test-pack", Enabled = true, Scope = "global", Steps = [] };
        var svc = new ReactionPackDashboardService(_context, _store, [def]);
        var model = await svc.GetDashboardModelAsync();
        Assert.Empty(model.ActivePacks);
        Assert.Single(model.InactivePacks);
        Assert.Equal("test-pack", model.InactivePacks[0].PackName);
    }

    [Fact]
    public async Task GetDashboardModel_TransitionsAppear_AcrossAllPacks()
    {
        await _store.RecordTransitionAsync("pack-a", 0, 1, "response.error_rate_5xx", 0.07);
        await _store.RecordTransitionAsync("pack-b", 0, 1, "response.rate_429", 0.04);
        var svc = new ReactionPackDashboardService(_context, _store, []);
        var model = await svc.GetDashboardModelAsync();
        Assert.Equal(2, model.RecentTransitions.Count);
    }

    [Fact]
    public async Task GetDashboardModel_ActivePack_AppearsInActiveNotInactive()
    {
        var def = new ReactionPackDefinition
        {
            Name = "active-pack",
            Enabled = true,
            Scope = "global",
            Steps = [new ReactionPackStep { Level = 1, Name = "elevated", Policy = "throttle-stealth" }]
        };
        _context.SetActiveLevel("active-pack", 1, "throttle-stealth", "global");
        var svc = new ReactionPackDashboardService(_context, _store, [def]);
        var model = await svc.GetDashboardModelAsync();
        Assert.Single(model.ActivePacks);
        Assert.Equal("active-pack", model.ActivePacks[0].PackName);
        Assert.Equal(1, model.ActivePacks[0].CurrentLevel);
        Assert.Equal("elevated", model.ActivePacks[0].CurrentLevelName);
        Assert.Empty(model.InactivePacks);
    }
}
