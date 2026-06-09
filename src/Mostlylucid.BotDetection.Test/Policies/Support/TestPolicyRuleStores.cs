using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Support;

/// <summary>
///     Shared <see cref="IPolicyRuleStore"/> test doubles. Lifted out of
///     <c>SbPolicyStackTests</c> so the resolver tests (and any future
///     policy-test class) can use the same primitives instead of each test
///     file re-inventing them.
///
///     The two wildcard baseline seed YAMLs added in 54b41133
///     (<c>wildcard-default-allow-human.yaml</c> + <c>wildcard-default-block-confirmed-bot.yaml</c>)
///     load via <see cref="YamlPolicyRuleStore.FromEmbeddedResources"/> at
///     boot. Tests that asserted exact rule counts pre-dated those seeds
///     and broke when they leaked in. These two stores let those tests
///     run against either a strictly-empty corpus or the legacy seed
///     trio (domain Allow + subdomain Challenge + endpoint Block).
/// </summary>
internal static class TestPolicyRuleStoreConstants
{
    public const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    public const string WildcardSeedTag = "wildcard-default-";
}

/// <summary>
///     Read-path-only <see cref="IPolicyRuleStore"/> that owns zero rules.
///     Used by tests that need a truly empty corpus -- the wildcard baseline
///     seeds embedded in <c>Mostlylucid.BotDetection.dll</c> would otherwise
///     leak in through <see cref="YamlPolicyRuleStore.FromEmbeddedResources"/>.
/// </summary>
internal sealed class EmptyPolicyRuleStore : IPolicyRuleStore
{
    private static readonly IReadOnlyList<PolicyRule> Empty = Array.Empty<PolicyRule>();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
        => Task.FromResult(Empty);

    public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
        => Task.FromResult(Empty);

    public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<PolicyRule?>(null);

    // No reloads ever fire from this store; the event is required by the
    // interface but never raised in tests.
#pragma warning disable CS0067
    public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
}

/// <summary>
///     Delegating <see cref="IPolicyRuleStore"/> that hides the wildcard
///     baseline seed rules (allow-human + block-confirmed-bot) added in
///     <c>54b41133</c>. Tests that were written against the legacy
///     three-rule corpus (domain Allow + subdomain Challenge + endpoint
///     Block) keep their contract without modifying production behaviour.
/// </summary>
internal sealed class LegacySeedOnlyPolicyRuleStore : IPolicyRuleStore
{
    private readonly YamlPolicyRuleStore _inner;

    public LegacySeedOnlyPolicyRuleStore()
    {
        _inner = YamlPolicyRuleStore.FromEmbeddedResources(typeof(PolicyRule).Assembly, TestPolicyRuleStoreConstants.SeedPrefix);
        _inner.Changed += (_, e) => Changed?.Invoke(this, e);
    }

    public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);

    public async Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
        => Filter(await _inner.GetRulesAtAsync(scope, ct));

    public async Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default)
        => Filter(await _inner.GetEffectiveRulesAsync(scopePath, ct));

    public async Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _inner.GetByIdAsync(id, ct);
        return rule is not null && IsWildcardSeed(rule) ? null : rule;
    }

    public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;

    private static IReadOnlyList<PolicyRule> Filter(IReadOnlyList<PolicyRule> rules)
    {
        // Common case: nothing to drop.
        var hasSeed = false;
        for (var i = 0; i < rules.Count; i++)
        {
            if (IsWildcardSeed(rules[i])) { hasSeed = true; break; }
        }
        if (!hasSeed) return rules;

        var filtered = new List<PolicyRule>(rules.Count);
        foreach (var rule in rules)
            if (!IsWildcardSeed(rule)) filtered.Add(rule);
        return filtered;
    }

    private static bool IsWildcardSeed(PolicyRule rule)
        => rule.Source.Contains(TestPolicyRuleStoreConstants.WildcardSeedTag, StringComparison.Ordinal);
}
