using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     <see cref="IConfigBaselineProvider"/> for a remote / thin-client dashboard: reads the
///     GATEWAY's composed config baseline over <c>GET /api/v1/policies/config-baseline</c> and
///     re-wraps each row as a config-source <see cref="EffectivePolicyRowViewModel"/>.
///
///     <para>
///     The thin client has no local config and no <c>IActionPolicyRegistry</c>, so it cannot compose
///     the baseline itself (upstream-not-authority). It never fabricates one: on any gateway failure
///     or an empty result it returns an empty list and the section renders nothing. <paramref name="canEdit"/>
///     is intentionally not forwarded -- the gateway composes read-only rows; a remote FOSS dashboard
///     is read-only, and the commercial overlay drives its own edit surface.
///     </para>
/// </summary>
internal sealed class RemoteConfigBaselineProvider : IConfigBaselineProvider
{
    private readonly GatewayApiClient _api;

    public RemoteConfigBaselineProvider(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<EffectivePolicyRowViewModel>> GetConfigRowsAsync(bool canEdit, CancellationToken ct = default)
    {
        var rows = await _api.GetEnvelopeAsync<IReadOnlyList<ConfigPolicyRowViewModel>>(
            "/api/v1/policies/config-baseline", ct);
        if (rows is null || rows.Count == 0)
            return [];
        return rows.Select(EffectivePolicyRowViewModel.ForConfig).ToList();
    }
}
