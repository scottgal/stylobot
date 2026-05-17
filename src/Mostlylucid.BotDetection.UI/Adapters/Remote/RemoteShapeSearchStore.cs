using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/investigate/shape-search</c> and
///     <c>/api/v1/investigate/presets</c>. Shape search is a commercial surface; remote
///     hosts opt in by registering this adapter, which silently 503s if the gateway
///     hasn't enabled it.
/// </summary>
internal sealed class RemoteShapeSearchStore : IShapeSearchStore
{
    private readonly GatewayApiClient _api;

    public RemoteShapeSearchStore(GatewayApiClient api) => _api = api;

    public async Task<InvestigationResult> SearchByShapeAsync(ShapeSearchFilter filter, CancellationToken ct = default)
    {
        var result = await _api.PostEnvelopeAsync<ShapeSearchFilter, InvestigationResult>(
            "/api/v1/investigate/shape-search", filter, ct);
        return result ?? EmptyResult();
    }

    public async Task<IReadOnlyList<InvestigationPreset>> GetPresetsAsync(CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<InvestigationPreset>("/api/v1/investigate/presets", ct);

    private static InvestigationResult EmptyResult() => new()
    {
        Summary = new InvestigationSummary()
    };
}
