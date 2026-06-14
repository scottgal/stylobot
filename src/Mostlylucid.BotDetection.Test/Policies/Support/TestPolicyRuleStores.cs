using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Support;

/// <summary>
///     Shared <see cref="IPolicyRuleStore"/> test doubles. Lifted out of
///     <c>SbPolicyStackTests</c> so the resolver tests (and any future
///     policy-test class) can use the same primitives instead of each test
///     file re-inventing them.
///
///     Wildcard-scope baseline seed YAMLs load via
///     <see cref="YamlPolicyRuleStore.FromEmbeddedResources"/> at boot.
///     Originally that was just the two added in 54b41133
///     (<c>wildcard-default-allow-human.yaml</c> +
///     <c>wildcard-default-block-confirmed-bot.yaml</c>); commit
///     <c>5dfe9f57</c> added <c>wildcard-allow-stylobot-internal.yaml</c>.
///     Tests that assert exact rule counts pre-date those seeds and break
///     when any leak in. The filter intentionally matches every
///     <c>wildcard-*.yaml</c> seed -- one prefix, every seed dropped --
///     so adding a new wildcard baseline doesn't require touching the
///     test infrastructure again. These two stores let those tests run
///     against either a strictly-empty corpus or the legacy seed trio
///     (domain Allow + subdomain Challenge + endpoint Block).
/// </summary>
internal static class TestPolicyRuleStoreConstants
{
    public const string SeedPrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";
    // Matches every wildcard-* seed YAML, not just wildcard-default-*. New
    // wildcard baselines (e.g. wildcard-allow-stylobot-internal.yaml from
    // commit 5dfe9f57) get filtered automatically without churning the
    // test-support code on each addition.
    public const string WildcardSeedTag = "wildcard-";
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

    public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
        => Task.FromResult(Empty);

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

    public async Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
        => Filter(await _inner.GetAllRulesAsync(ct));

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

/// <summary>
///     Read-path <see cref="IPolicyRuleStore"/> that returns a fixed list of
///     rules supplied at construction. Replaces the per-test-file
///     <c>InMemoryPolicyRuleStore</c> private classes that
///     <see cref="PolicyEditPresenterTests"/> and
///     <see cref="PolicyStackSummaryBuilderTests"/> were each carrying;
///     "Fixed" is more honest than "InMemory" -- there is no put / delete
///     surface, just the immutable seed list the constructor took.
///     <para>
///         Mirrors the <see cref="IPolicyRuleStore"/> read methods enough for
///         <see cref="PolicyEditPresenter"/> (only calls
///         <see cref="GetByIdAsync"/>) and
///         <see cref="PolicyStackSummaryBuilder"/> (only calls
///         <see cref="GetEffectiveRulesAsync"/>) -- the remaining methods
///         are stubbed with the obvious matches so future tests can reuse
///         the type without surprises.
///     </para>
/// </summary>
internal sealed class FixedRulePolicyRuleStore : IPolicyRuleStore
{
    private readonly IReadOnlyList<PolicyRule> _rules;

    public FixedRulePolicyRuleStore(params PolicyRule[] rules)
    {
        _rules = rules;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default)
    {
        IReadOnlyList<PolicyRule> matched = _rules.Where(r => r.Scope == scope).ToArray();
        return Task.FromResult(matched);
    }

    public Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(
        IReadOnlyList<PolicyScope> scopePath,
        CancellationToken ct = default)
    {
        var result = new List<PolicyRule>(_rules.Count);
        foreach (var scope in scopePath)
        {
            foreach (var rule in _rules)
            {
                if (rule.Scope == scope) result.Add(rule);
            }
        }
        return Task.FromResult<IReadOnlyList<PolicyRule>>(result);
    }

    public Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult<PolicyRule?>(_rules.FirstOrDefault(r => r.Id == id));

    public Task<IReadOnlyList<PolicyRule>> GetAllRulesAsync(CancellationToken ct = default)
        => Task.FromResult(_rules);

#pragma warning disable CS0067 // Event required by interface, never raised here.
    public event EventHandler<PolicyRuleStoreChangedEventArgs>? Changed;
#pragma warning restore CS0067
}
