using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Supplies the config-baseline rows (<c>BotTypeActionPolicies</c> + <c>DefaultActionPolicyName</c>
///     -> action policy) for the <c>/dashboard/policies</c> effective view, abstracting over WHERE
///     the baseline is computed.
///
///     <para>
///     On a LOCAL-detection dashboard the baseline is local config, composed in-process by
///     <see cref="EffectivePolicyComposer"/> (it needs the real <c>IActionPolicyRegistry</c> to map
///     action names to colour-carrying <c>ActionType</c>s). A REMOTE / thin-client dashboard has no
///     local baseline -- its config is empty -- so it reads the GATEWAY's composed rows over the
///     REST read surface via a remote implementation. Either way the view renders the same rows; it
///     never fabricates an empty local baseline (the null-object regression class).
///     </para>
/// </summary>
public interface IConfigBaselineProvider
{
    /// <summary>
    ///     The composed config-baseline rows. <paramref name="canEdit"/> rides the same
    ///     <c>IPolicyCanEditPolicy.CanEdit</c> gate as the policy rows (FOSS read-only). Async because
    ///     the remote implementation fetches over HTTP; the local composer completes synchronously.
    /// </summary>
    Task<IReadOnlyList<EffectivePolicyRowViewModel>> GetConfigRowsAsync(bool canEdit, CancellationToken ct = default);
}
