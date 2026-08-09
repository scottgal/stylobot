using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

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
    public void Phase2_BuiltInRateLimitPoliciesAreRegistered()
    {
        // Phase 2 adds four rate-limit built-ins to ActionPolicyRegistry's
        // RegisterBuiltInPolicies block. Pin their presence + intent so a
        // future rename or removal trips this test instead of silently
        // disappearing.
        var registry = BuildRegistryWith();
        var provider = new RegistryPolicyStateProvider(registry);
        var states = provider.GetAll();

        var expected = new[] { "rate-limit-search", "rate-limit-ai", "rate-limit-social", "rate-limit-monitoring" };
        foreach (var name in expected)
        {
            var s = states.SingleOrDefault(x => x.Name == name);
            Assert.NotNull(s);
            Assert.Equal(PolicyIntent.RateLimit, s!.Intent);
            Assert.True(s.EffectiveParams.ContainsKey("requestsPerMinute"));
            Assert.True(s.EffectiveParams.ContainsKey("burstSize"));
            Assert.True(s.EffectiveParams.ContainsKey("overLimitAction"));
            Assert.True(s.EffectiveParams.ContainsKey("keyBy"));
        }
    }

    [Fact]
    public void Phase2_RateLimitSearch_SurfacesExpectedDefaults()
    {
        var registry = BuildRegistryWith();
        var provider = new RegistryPolicyStateProvider(registry);
        var search = provider.Get("rate-limit-search");

        Assert.NotNull(search);
        Assert.Equal(60, search!.EffectiveParams["requestsPerMinute"]);
        Assert.Equal(10, search.EffectiveParams["burstSize"]);
        Assert.Equal("throttle-status", search.EffectiveParams["overLimitAction"]);
    }

    [Fact]
    public void Phase2_RateLimitAi_BouncesHarder_BlockSoftFallback()
    {
        var registry = BuildRegistryWith();
        var provider = new RegistryPolicyStateProvider(registry);
        var ai = provider.Get("rate-limit-ai");

        Assert.NotNull(ai);
        Assert.Equal(10, ai!.EffectiveParams["requestsPerMinute"]);
        Assert.Equal("block-soft", ai.EffectiveParams["overLimitAction"]);
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

    // ---------------------------------------------------------------------
    // IPolicyStateContributor seam (pack-owned policies contribute their
    // effective runtime state without core referencing the pack).
    // ---------------------------------------------------------------------

    [Fact]
    public void ContributorParams_AreMergedIntoPolicyState()
    {
        var registry = BuildRegistryWith(new ContributingPolicy("contrib-a"));
        var provider = new RegistryPolicyStateProvider(registry);

        var state = provider.Get("contrib-a");

        Assert.NotNull(state);
        Assert.Equal("html", state!.EffectiveParams["representation"]);
        Assert.Equal(128, state.EffectiveParams["maxEntries"]);
        Assert.Equal(7L, state.EffectiveParams["hits"]);
        // The base actionType entry survives the merge.
        Assert.Equal(ActionType.Custom.ToString(), state.EffectiveParams["actionType"]);
    }

    [Fact]
    public void ContributorFiringStats_AreHonouredWhenNonNull()
    {
        var registry = BuildRegistryWith(new ContributingPolicy("contrib-b", stats: new PolicyFiringStats(12, 3, 1.0)));
        var provider = new RegistryPolicyStateProvider(registry);

        var state = provider.Get("contrib-b");

        Assert.NotNull(state);
        Assert.Equal(12, state!.Stats.Hits5m);
        Assert.Equal(3, state.Stats.DistinctSignatures5m);
    }

    [Fact]
    public void Contributor_WithoutStats_KeepsEmptyStats()
    {
        var registry = BuildRegistryWith(new ContributingPolicy("contrib-c"));
        var provider = new RegistryPolicyStateProvider(registry);

        var state = provider.Get("contrib-c");

        Assert.NotNull(state);
        Assert.Equal(0, state!.Stats.Hits5m);
    }

    /// <summary>Test-only policy implementing the contributor seam, standing in for a pack policy.</summary>
    private sealed class ContributingPolicy : IActionPolicy, IPolicyStateContributor
    {
        private readonly PolicyFiringStats? _stats;

        public ContributingPolicy(string name, PolicyFiringStats? stats = null)
        {
            Name = name;
            _stats = stats;
        }

        public string Name { get; }
        public ActionType ActionType => ActionType.Custom;
        public PolicyIntent Intent => PolicyIntent.Pass;

        public Task<ActionResult> ExecuteAsync(
            Microsoft.AspNetCore.Http.HttpContext context,
            AggregatedEvidence evidence,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ActionResult.Allowed("contrib"));

        public IReadOnlyDictionary<string, object> EffectiveParams => new Dictionary<string, object>
        {
            ["representation"] = "html",
            ["maxEntries"] = 128,
            ["hits"] = 7L,
        };

        public PolicyFiringStats? FiringStats => _stats;
    }
}
