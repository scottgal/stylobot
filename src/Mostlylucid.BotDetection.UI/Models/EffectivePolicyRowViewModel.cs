namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Provenance of one row in the /dashboard/policies effective view. The policies page
///     composes ONE set of rows over two layers so config-derived enforcement stops being
///     invisible (the operator hit a silent throttle that was a config default, not a
///     hand-rolled rule -- see <c>BotTypeActionPolicies["Scraper"] = "throttle-aggressive"</c>).
/// </summary>
public enum EffectivePolicyRowSource
{
    /// <summary>Config baseline -- always enforced, from <c>BotDetectionOptions</c>.</summary>
    Config,

    /// <summary>A hand-authored <c>PolicyRule</c> (the existing SbPolicyStack rows).</summary>
    Policy
}

/// <summary>
///     One row in the composed effective-policy set. Exactly one payload is non-null,
///     selected by <see cref="Source"/>. Policy rows reuse the existing rich
///     <see cref="PolicyStackRowViewModel"/> unchanged; config rows carry only what a
///     config baseline actually is (a bot-type / default -> action-policy mapping), so they
///     never synthesise the ~20 rule-shaped fields (predicate, scope, latency, template
///     origin) that would be meaningless on a config default.
/// </summary>
/// <param name="Source">Which layer this row came from.</param>
/// <param name="PolicyRow">Non-null when <see cref="Source"/> is <see cref="EffectivePolicyRowSource.Policy"/>.</param>
/// <param name="ConfigRow">Non-null when <see cref="Source"/> is <see cref="EffectivePolicyRowSource.Config"/>.</param>
public sealed record EffectivePolicyRowViewModel(
    EffectivePolicyRowSource Source,
    PolicyStackRowViewModel? PolicyRow = null,
    ConfigPolicyRowViewModel? ConfigRow = null)
{
    /// <summary>Wrap a config baseline row.</summary>
    public static EffectivePolicyRowViewModel ForConfig(ConfigPolicyRowViewModel row) =>
        new(EffectivePolicyRowSource.Config, ConfigRow: row);

    /// <summary>Wrap an existing hand-authored policy row.</summary>
    public static EffectivePolicyRowViewModel ForPolicy(PolicyStackRowViewModel row) =>
        new(EffectivePolicyRowSource.Policy, PolicyRow: row);
}

/// <summary>
///     One config-baseline row: a bot-type (or the global default) mapped to the action policy
///     the orchestrator will apply, with the provenance the commercial edit control needs to
///     target an override. Lean by design -- a config default has no predicate, scope, latency,
///     or template origin.
/// </summary>
/// <param name="Target">
///     Display target -- a <c>BotType</c> name (<c>"Scraper"</c>) for a
///     <see cref="EffectivePolicyConfigSource.BotTypeActionPolicies"/> row, or
///     <c>"(default)"</c> for the global fallback.
/// </param>
/// <param name="ResultingAction">The action-policy name enforced for this target (<c>"throttle-aggressive"</c>).</param>
/// <param name="ActionColorClass">
///     Canonical dashboard verdict colour class -- <c>verdict-error</c> / <c>verdict-warning</c> /
///     <c>verdict-info</c> / <c>verdict-success</c>. Derived from the policy's
///     <c>ActionType</c> via the registry, never from the name string.
/// </param>
/// <param name="ConfigKey">
///     The <c>IConfiguration</c> key this value binds to (<c>"BotDetection:BotTypeActionPolicies:Scraper"</c>),
///     so the commercial edit control can target the override write. Empty is never valid.
/// </param>
/// <param name="Source">Which config section the row came from.</param>
/// <param name="SupersededByRuleId">
///     Non-null when a hand-authored <see cref="PolicyStackRowViewModel"/> overrides this target's
///     effective action. Populated by <c>IEffectivePolicyConfigOverlay</c> (the commercial overlay
///     resolves the effective action across policy + config); the FOSS passthrough leaves it null,
///     so a FOSS host shows the honest config baseline and the hand-rolled rules side by side.
/// </param>
/// <param name="CanEdit">
///     When <c>true</c>, the row renders the edit affordance targeting <see cref="ConfigKey"/>.
///     Threaded from the same <c>IPolicyCanEditPolicy.CanEdit</c> flag as the policy rows: FOSS
///     (<c>AlwaysReadOnly</c>) renders read-only; commercial (<c>LicenseAware</c>) enables edit.
/// </param>
public sealed record ConfigPolicyRowViewModel(
    string Target,
    string ResultingAction,
    string ActionColorClass,
    string ConfigKey,
    EffectivePolicyConfigSource Source,
    Guid? SupersededByRuleId = null,
    bool CanEdit = false,
    bool ShowReadOnlyEditAffordance = false);

/// <summary>Which <c>BotDetectionOptions</c> section a config-baseline row came from.</summary>
public enum EffectivePolicyConfigSource
{
    /// <summary><c>BotDetectionOptions.BotTypeActionPolicies</c> (bot-type -> action-policy).</summary>
    BotTypeActionPolicies,

    /// <summary><c>BotDetectionOptions.DefaultActionPolicyName</c> (the global fallback).</summary>
    DefaultActionPolicy,

    /// <summary>Post-detection, first-match-wins rule from <c>DetectionPolicies:Rules</c>.</summary>
    DetectionPolicyRule
}
