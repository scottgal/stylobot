using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackTransitionStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ReactionPackTransitionStore _store;

    public ReactionPackTransitionStoreTests()
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
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();

    [Fact]
    public async Task RecordTransition_InsertsRow()
    {
        await _store.RecordTransitionAsync("test-pack", fromLevel: 0, toLevel: 1,
            triggeredBy: "response.error_rate_5xx", signalValue: 0.12);

        var transitions = await _store.GetRecentTransitionsAsync("test-pack", limit: 10);
        Assert.Single(transitions);
        Assert.Equal("test-pack", transitions[0].PackName);
        Assert.Equal(0, transitions[0].FromLevel);
        Assert.Equal(1, transitions[0].ToLevel);
        Assert.Equal("response.error_rate_5xx", transitions[0].TriggeredBy);
        Assert.Equal(0.12, transitions[0].SignalValue, precision: 6);
    }

    [Fact]
    public async Task GetRecentTransitions_ReturnsLatestFirst()
    {
        await _store.RecordTransitionAsync("pack-a", 0, 1, "signal.a", 0.1);
        await _store.RecordTransitionAsync("pack-a", 1, 2, "signal.b", 0.2);

        var transitions = await _store.GetRecentTransitionsAsync("pack-a", limit: 10);
        Assert.Equal(2, transitions.Count);
        Assert.Equal(2, transitions[0].ToLevel);
    }

    [Fact]
    public async Task GetLatestActiveLevel_NoTransitions_ReturnsZero()
    {
        Assert.Equal(0, await _store.GetLatestActiveLevelAsync("nonexistent-pack"));
    }

    [Fact]
    public async Task GetLatestActiveLevel_AfterEscalation_ReturnsCurrentLevel()
    {
        await _store.RecordTransitionAsync("my-pack", 0, 1, "signal.x", 0.15);
        await _store.RecordTransitionAsync("my-pack", 1, 2, "signal.x", 0.35);
        Assert.Equal(2, await _store.GetLatestActiveLevelAsync("my-pack"));
    }
}
