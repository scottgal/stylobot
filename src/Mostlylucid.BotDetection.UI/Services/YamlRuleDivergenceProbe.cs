using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     FOSS default <see cref="IRuleDivergenceProbe"/>. Reads
///     <see cref="YamlPolicyRuleStore.DivergedRuleIds"/> directly. The
///     YAML store keeps this set in-memory only -- a process restart
///     wipes it, so the FIRST reload after restart cannot distinguish
///     "operator edited" from "fresh materialization". The commercial
///     Postgres overlay replaces this probe with one that consults the
///     persistent <c>policy_rules.override_diverged</c> column.
///
///     <para>
///         The probe accepts <see cref="IPolicyRuleStore"/> rather than
///         the concrete YAML store so the DI registration is permissive:
///         when no template materialization is wired (the simpler FOSS
///         host that loads bare rules only), the probe binds to an
///         arbitrary store and reports every rule as non-divergent. The
///         dashboard banner therefore never renders on bare-rules hosts,
///         which is the right behaviour.
///     </para>
/// </summary>
public sealed class YamlRuleDivergenceProbe : IRuleDivergenceProbe
{
    private readonly YamlPolicyRuleStore? _yamlStore;

    /// <summary>
    ///     Bind to the YAML store via the generic <see cref="IPolicyRuleStore"/>
    ///     interface. Non-YAML implementations (e.g. the commercial Postgres
    ///     store) cause the probe to short-circuit to "never diverged" so
    ///     the FOSS default never lies in remote-mode hosts.
    /// </summary>
    public YamlRuleDivergenceProbe(IPolicyRuleStore store)
    {
        _yamlStore = store as YamlPolicyRuleStore;
    }

    /// <inheritdoc />
    public bool IsDiverged(Guid ruleId)
        => _yamlStore is not null && _yamlStore.DivergedRuleIds.Contains(ruleId);
}
