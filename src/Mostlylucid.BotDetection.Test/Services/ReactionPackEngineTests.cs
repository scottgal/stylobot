using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackEngineTests : IDisposable
{
    private readonly ReactionPackContext _context = new();
    private readonly DegradationAtom _atom = new(windowSeconds: 60, emaAlpha: 0.5);
    private readonly SignalGroupRegistry _groupRegistry = new([]);
    private readonly SqliteConnection _dbConn;
    private readonly ReactionPackTransitionStore _transitionStore;

    public ReactionPackEngineTests()
    {
        _dbConn = new SqliteConnection("Data Source=:memory:");
        _dbConn.Open();
        using var cmd = _dbConn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE reaction_pack_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                pack_name TEXT NOT NULL,
                from_level INTEGER NOT NULL,
                to_level INTEGER NOT NULL,
                triggered_by TEXT NOT NULL,
                signal_value REAL NOT NULL,
                occurred_at INTEGER NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
        _transitionStore = new ReactionPackTransitionStore(_dbConn);
    }

    public void Dispose()
    {
        _atom.Dispose();
        _dbConn.Dispose();
    }

    private ReactionRuleEvaluator Evaluator() => new(_groupRegistry);

    private ReactionPackEngine BuildEngine(ReactionPackDefinition pack) =>
        new([pack], _atom, _context, Evaluator(), _transitionStore, NullLogger<ReactionPackEngine>.Instance);

    private static ReactionPackDefinition ImmediatePack(string policyName = "throttle-gentle") =>
        new()
        {
            Name = "test-pack",
            Enabled = true,
            Scope = "global",
            Steps =
            [
                new ReactionPackStep
                {
                    Level = 1,
                    Name = "watch",
                    Policy = policyName,
                    Activate = new ReactionConditionSet
                    {
                        Condition = "any",
                        Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 0.0 }]
                    },
                    Deactivate = new ReactionConditionSet
                    {
                        Condition = "all",
                        Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 0.0 }]
                    }
                }
            ]
        };

    [Fact]
    public void GetOverridePolicy_NoPack_ReturnsNull()
    {
        Assert.Null(_context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ActiveGlobalPack_ReturnsPackPolicy()
    {
        _context.SetActiveLevel("test-pack", 1, "throttle-gentle", "global");
        Assert.Equal("throttle-gentle", _context.GetOverridePolicy("/api/anything", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ActiveEndpointPack_OnlyMatchesEndpoint()
    {
        _context.SetActiveLevel("checkout-pack", 1, "challenge-pow", "/api/checkout");
        Assert.Equal("challenge-pow", _context.GetOverridePolicy("/api/checkout", "block"));
        Assert.Null(_context.GetOverridePolicy("/api/users", "block"));
    }

    [Fact]
    public void GetOverridePolicy_DeactivatedPack_ReturnsNull()
    {
        _context.SetActiveLevel("test-pack", 1, "throttle-gentle", "global");
        _context.Deactivate("test-pack");
        Assert.Null(_context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public void GetOverridePolicy_ConflictingPacks_HigherPriorityWins()
    {
        _context.SetActiveLevel("low-priority", 1, "throttle-gentle", "global", priority: 0);
        _context.SetActiveLevel("high-priority", 1, "block-soft", "global", priority: 10);
        Assert.Equal("block-soft", _context.GetOverridePolicy("/api/test", "block"));
    }

    [Fact]
    public async Task EvaluatePack_EscalatesToLevel1_WhenConditionSatisfied()
    {
        var engine = BuildEngine(ImmediatePack());

        // With emaAlpha=0.5, a single 500 gives value=0.5 which is >0.05
        _atom.RecordResponse(500, 50, "/test");
        await engine.EvaluateNowAsync();

        Assert.Equal("throttle-gentle", _context.GetOverridePolicy("/api/anything", null));
    }

    [Fact]
    public async Task EvaluatePack_DeescalatesWhenSignalDrops()
    {
        var engine = BuildEngine(ImmediatePack());

        _atom.RecordResponse(500, 50, "/test");
        await engine.EvaluateNowAsync();
        Assert.NotNull(_context.GetOverridePolicy("/api/anything", null));

        // Flood with 200s; with alpha=0.5 after enough 200s the EMA drops below 0.02
        for (var i = 0; i < 10; i++) _atom.RecordResponse(200, 50, "/test");
        await engine.EvaluateNowAsync();

        Assert.Null(_context.GetOverridePolicy("/api/anything", null));
    }
}
