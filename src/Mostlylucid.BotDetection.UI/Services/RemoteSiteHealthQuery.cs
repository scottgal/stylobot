using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Adapters.Remote;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Remote-mode <see cref="ISiteHealthQuery"/>: thin client over
///     <c>GET /api/v1/site-health/history</c>. Mirrors the
///     <c>RemoteDashboardEventStore</c> pattern so remote dashboard hosts
///     (marketing site, commercial control plane) reach the gateway's bounded
///     history ring with the same envelope semantics as every other Remote*
///     store.
/// </summary>
internal sealed class RemoteSiteHealthQuery : ISiteHealthQuery
{
    private readonly GatewayApiClient _api;

    public RemoteSiteHealthQuery(GatewayApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public async Task<IReadOnlyList<DegradationSnapshot>> GetHistoryAsync(
        string window, CancellationToken ct = default)
    {
        var path = $"/api/v1/site-health/history?window={Uri.EscapeDataString(window ?? "1h")}";
        var list = await _api.GetEnvelopeListAsync<DegradationSnapshot>(path, ct);
        return list;
    }
}
