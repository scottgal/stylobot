using System.Reflection;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Templates;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     T4 Part B coverage for the read-side divergence probe contract.
///     The YAML default reads <see cref="YamlPolicyRuleStore.DivergedRuleIds"/>
///     directly; bare-rules hosts (no template materialization wired) and
///     non-YAML stores report every rule as non-divergent so the dashboard
///     banner never lies.
/// </summary>
public sealed class RuleDivergenceProbeTests
{
    [Fact]
    public void Probe_returns_false_for_non_yaml_store()
    {
        var probe = new YamlRuleDivergenceProbe(new StubStore());
        Assert.False(probe.IsDiverged(Guid.NewGuid()));
    }

    [Fact]
    public async Task Probe_reads_yaml_store_diverged_set()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(PolicyRule).Assembly,
            "Mostlylucid.BotDetection.Policies.Rules.SeedRules.");
        await store.InitializeAsync();

        var probe = new YamlRuleDivergenceProbe(store);
        // No materialization sweep ran -- empty set on a freshly loaded
        // store with no template applications wired.
        Assert.False(probe.IsDiverged(Guid.NewGuid()));
    }

    // ---- Stub store -----------------------------------------------------

    private sealed class StubStore : IPolicyRuleStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
        public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
        public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<PolicyRule?>(null);
        public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PolicyRule>>(Array.Empty<PolicyRule>());
        public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }
    }
}
