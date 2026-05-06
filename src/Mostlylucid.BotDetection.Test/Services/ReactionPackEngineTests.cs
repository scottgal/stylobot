using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackEngineTests : IDisposable
{
    private readonly ReactionPackContext _context = new();
    private readonly DegradationAtom _atom = new(windowSeconds: 60, emaAlpha: 0.5);
    private readonly SignalGroupRegistry _groupRegistry = new([]);

    public void Dispose() => _atom.Dispose();

    private ReactionRuleEvaluator Evaluator() => new(_groupRegistry);

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
    public void EvaluatePack_EscalatesToLevel1_WhenConditionSatisfied()
    {
        var pack = ImmediatePack();
        var engine = new ReactionPackEngine(
            [pack], _atom, _context, Evaluator(),
            NullLogger<ReactionPackEngine>.Instance);

        // With emaAlpha=0.5, a single 500 gives value=0.5 which is >0.05
        _atom.RecordResponse(500, 50, "/test");
        engine.EvaluateNow();

        Assert.Equal("throttle-gentle", _context.GetOverridePolicy("/api/anything", null));
    }

    [Fact]
    public void EvaluatePack_DeescalatesWhenSignalDrops()
    {
        var pack = ImmediatePack();
        var engine = new ReactionPackEngine(
            [pack], _atom, _context, Evaluator(),
            NullLogger<ReactionPackEngine>.Instance);

        _atom.RecordResponse(500, 50, "/test");
        engine.EvaluateNow();
        Assert.NotNull(_context.GetOverridePolicy("/api/anything", null));

        // Flood with 200s; with alpha=0.5 after enough 200s the EMA drops below 0.02
        for (var i = 0; i < 10; i++) _atom.RecordResponse(200, 50, "/test");
        engine.EvaluateNow();

        Assert.Null(_context.GetOverridePolicy("/api/anything", null));
    }
}
