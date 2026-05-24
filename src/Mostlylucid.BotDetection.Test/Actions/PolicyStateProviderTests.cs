using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Phase-1 contract pins for <see cref="IPolicyStateProvider"/> / its
///     baseline <see cref="RegistryPolicyStateProvider"/> implementation.
/// </summary>
public class PolicyStateProviderTests
{
    [Fact]
    public void GetAll_ReturnsEntryPerRegisteredPolicy()
    {
        // The registry also auto-registers built-ins from BotDetectionOptions
        // defaults (block / block-hard / throttle / throttle-stealth / etc.)
        // -- the provider just walks whatever is there, so we check our
        // additions are present alongside the implementation-defined defaults.
        var registry = BuildRegistryWith(
            new BlockActionPolicy("block-a", new BlockActionOptions { StatusCode = 403, Message = "x" }),
            new ThrottleActionPolicy("throttle-a", new ThrottleActionOptions { BaseDelayMs = 100 }),
            new LogOnlyActionPolicy("logonly-a", new LogOnlyActionOptions()));
        var provider = new RegistryPolicyStateProvider(registry);

        var states = provider.GetAll();

        Assert.Contains(states, s => s.Name == "block-a" && s.Intent == PolicyIntent.Block);
        Assert.Contains(states, s => s.Name == "throttle-a" && s.Intent == PolicyIntent.Throttle);
        Assert.Contains(states, s => s.Name == "logonly-a" && s.Intent == PolicyIntent.Pass);
    }

    [Fact]
    public void Get_ByName_FindsRegisteredPolicy()
    {
        var registry = BuildRegistryWith(
            new BlockActionPolicy("hard-block", new BlockActionOptions { StatusCode = 403, Message = "x" }));
        var provider = new RegistryPolicyStateProvider(registry);

        var state = provider.Get("hard-block");

        Assert.NotNull(state);
        Assert.Equal("hard-block", state!.Name);
        Assert.Equal(PolicyIntent.Block, state.Intent);
    }

    [Fact]
    public void Get_MissingName_ReturnsNull()
    {
        var registry = BuildRegistryWith();
        var provider = new RegistryPolicyStateProvider(registry);

        Assert.Null(provider.Get("nope"));
    }

    [Fact]
    public void Phase1_StatsAreZero_TierIsAbsent()
    {
        // The point of phase 1 is "no behaviour change" -- the provider has
        // to expose the new contract but doesn't gain real firing data
        // until phase 2 (rate-limit primitive) and phase 4 (adaptive
        // scaling). Pin the zero state so a future contributor doesn't
        // silently start surfacing fake data.
        var registry = BuildRegistryWith(
            new ThrottleActionPolicy("phase1-pin", new ThrottleActionOptions { BaseDelayMs = 100 }));
        var provider = new RegistryPolicyStateProvider(registry);

        var state = provider.Get("phase1-pin");

        Assert.NotNull(state);
        Assert.Null(state!.CurrentTier);
        Assert.Null(state.TierEnteredAtUtc);
        Assert.Equal(0, state.Stats.Hits5m);
        Assert.Equal(0, state.Stats.DistinctSignatures5m);
        Assert.Null(state.Stats.CurrentMultiplier);
    }

    private static ActionPolicyRegistry BuildRegistryWith(params IActionPolicy[] policies)
    {
        // Minimal options + empty factory list -- the built-in default
        // policies the registry registers from BotDetectionOptions get
        // included alongside our test policies. The assertions below use
        // Contains / specific lookups, so we don't need to filter them out.
        // (Phase 2 will introduce a test-only registry constructor; for
        // now keep this scoped to what the tests actually assert.)
        var registry = new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            policies);
        return registry;
    }
}
