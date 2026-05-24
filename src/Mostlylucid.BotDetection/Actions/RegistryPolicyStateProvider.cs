using System.Collections.Frozen;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Phase-1 baseline implementation of <see cref="IPolicyStateProvider"/>.
///     Walks the action policy registry and emits a <see cref="PolicyState"/>
///     per registered policy with empty firing stats and no degradation tier.
/// </summary>
/// <remarks>
///     <para>
///         Phase 1 ships the scaffolding so the dashboard tab + downstream
///         consumers can be built against a stable contract before the
///         rate-limit primitive (phase 2) or the adaptive scaling tracker
///         (phase 4) land. Replace with a richer provider once those pieces
///         have data to surface.
///     </para>
///     <para>
///         <c>EffectiveParams</c> here is intentionally minimal -- just the
///         <see cref="ActionType"/> string. Each action policy class will
///         contribute its own params in phase 5 (policy detail UI).
///     </para>
/// </remarks>
public sealed class RegistryPolicyStateProvider : IPolicyStateProvider
{
    private static readonly PolicyFiringStats EmptyStats = new(0, 0, null);
    private readonly IActionPolicyRegistry _registry;

    public RegistryPolicyStateProvider(IActionPolicyRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<PolicyState> GetAll()
    {
        var all = _registry.GetAllPolicies();
        var states = new List<PolicyState>(all.Count);
        foreach (var (_, policy) in all)
        {
            states.Add(ToState(policy));
        }
        return states;
    }

    public PolicyState? Get(string policyName)
    {
        var policy = _registry.GetPolicy(policyName);
        return policy is null ? null : ToState(policy);
    }

    private static PolicyState ToState(IActionPolicy policy)
    {
        var paramsMap = new Dictionary<string, object>
        {
            ["actionType"] = policy.ActionType.ToString(),
        }.ToFrozenDictionary();

        return new PolicyState(
            Name: policy.Name,
            Intent: policy.Intent,
            EffectiveParams: paramsMap,
            CurrentTier: null,
            TierEnteredAtUtc: null,
            Stats: EmptyStats);
    }
}
