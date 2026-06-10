namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Read interface for the policy rule corpus. Implementations may back
///     onto embedded YAML (FOSS default), on-disk YAML with file-watcher
///     reloads (FOSS dev loop), or commercial Postgres + Redis broadcast.
///     The resolver, evaluator, and dashboard read-paths depend only on
///     this surface.
/// </summary>
public interface IPolicyRuleStore
{
    /// <summary>
    ///     Load every rule into memory. Idempotent; safe to call multiple times.
    ///     Implementations should complete reading durable storage before returning.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    ///     Returns rules whose <see cref="PolicyRule.Scope"/> EQUALS
    ///     <paramref name="scope"/>. Ancestors and descendants are NOT
    ///     included -- callers that want the effective walk should use
    ///     <see cref="GetEffectiveRulesAsync"/>.
    /// </summary>
    Task<IReadOnlyList<PolicyRule>> GetRulesAtAsync(PolicyScope scope, CancellationToken ct = default);

    /// <summary>
    ///     Walk the supplied scope path (most-specific first) and return the
    ///     concatenated rule list. Within each scope, rules are ordered by
    ///     <see cref="PolicyRule.Priority"/> ascending. Disabled and Draft
    ///     rules are still returned -- filtering by mode is the evaluator's
    ///     responsibility.
    /// </summary>
    Task<IReadOnlyList<PolicyRule>> GetEffectiveRulesAsync(IReadOnlyList<PolicyScope> scopePath, CancellationToken ct = default);

    /// <summary>
    ///     Look up a single rule by id. Returns <c>null</c> when the id is
    ///     unknown.
    /// </summary>
    Task<PolicyRule?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Raised after a reload changes the rule corpus. The supplied scope
    ///     is the broadest scope known to be affected -- a per-rule edit fires
    ///     with that rule's own scope; a bulk reload may fire with the
    ///     wildcard scope (<c>PolicyScope.Wildcard()</c>).
    /// </summary>
    event EventHandler<PolicyRuleStoreChangedEventArgs> Changed;
}

/// <summary>
///     Event payload for <see cref="IPolicyRuleStore.Changed"/>.
/// </summary>
public sealed class PolicyRuleStoreChangedEventArgs(PolicyScope changedScope) : EventArgs
{
    /// <summary>Broadest scope known to be affected by the change.</summary>
    public PolicyScope ChangedScope { get; } = changedScope;
}
