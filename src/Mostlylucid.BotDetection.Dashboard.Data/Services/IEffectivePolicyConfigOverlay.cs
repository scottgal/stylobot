using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Contribution seam over each config-baseline row of the /dashboard/policies effective view.
///     Mirrors the <c>IDashboardDatasetExtension</c> composer pattern: the FOSS composer produces the
///     raw config-default rows and calls <see cref="Apply"/> per row; a commercial package registers
///     the real overlay to (a) replace <see cref="ConfigPolicyRowViewModel.ResultingAction"/> with the
///     licensed config override, (b) stamp <see cref="ConfigPolicyRowViewModel.SupersededByRuleId"/>
///     from the effective-policy resolver, and (c) flip <see cref="ConfigPolicyRowViewModel.CanEdit"/>
///     under the license gate. Contribution, not decorator: the FOSS host keeps a no-op default and is
///     never replaced, so a FOSS deployment shows the honest config baseline (read-only) and a
///     commercial deployment shows the overridden, editable value -- additive, never degrading.
/// </summary>
public interface IEffectivePolicyConfigOverlay
{
    /// <summary>
    ///     Return <paramref name="baseRow"/> transformed for the current host/license. The FOSS
    ///     default returns it verbatim. Must never return null.
    /// </summary>
    ConfigPolicyRowViewModel Apply(ConfigPolicyRowViewModel baseRow);
}

/// <summary>
///     FOSS default overlay: identity. The config baseline renders exactly as read from
///     <c>BotDetectionOptions</c>, read-only, with no override and no superseded stamp. Registered
///     via <c>TryAdd</c> so a commercial host's real overlay wins without a replace-registration.
/// </summary>
public sealed class PassthroughEffectivePolicyConfigOverlay : IEffectivePolicyConfigOverlay
{
    /// <inheritdoc />
    public ConfigPolicyRowViewModel Apply(ConfigPolicyRowViewModel baseRow) => baseRow;
}
