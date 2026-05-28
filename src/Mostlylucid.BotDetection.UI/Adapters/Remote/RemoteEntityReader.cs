using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only <see cref="IEntityReader"/> proxying the gateway's
///     <c>/api/v1/entities/*</c> surface. Backs the dashboard's entity-detail
///     route (<c>/dashboard/entity/{id}</c>) on remote-mode hosts that don't
///     run detection or own a session store.
/// </summary>
internal sealed class RemoteEntityReader : IEntityReader
{
    private readonly GatewayApiClient _api;

    public RemoteEntityReader(GatewayApiClient api) => _api = api;

    public async Task<string?> LookupEntityIdAsync(string primarySignature, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<string>(
            $"/api/v1/entities/lookup/{Uri.EscapeDataString(primarySignature)}", ct);

    public async Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
        => await _api.GetEnvelopeAsync<ResolvedEntity>(
            $"/api/v1/entities/{Uri.EscapeDataString(entityId)}", ct);

    public async Task<IReadOnlyList<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<EntityEdge>(
            $"/api/v1/entities/{Uri.EscapeDataString(entityId)}/edges", ct);
}
