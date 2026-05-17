using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/endpoint-pins</c>. Add / Remove throw because
///     the gateway that owns the pin database is the only one that can mutate.
/// </summary>
internal sealed class RemotePinnedEndpointStore : IPinnedEndpointStore
{
    private readonly GatewayApiClient _api;

    public RemotePinnedEndpointStore(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default)
        => await _api.GetEnvelopeListAsync<PinnedEndpoint>("/api/v1/endpoint-pins", ct);

    public Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default)
        => throw new NotSupportedException("Endpoint-pin writes are not supported against a remote gateway.");

    public Task<bool> RemoveAsync(long id, CancellationToken ct = default)
        => throw new NotSupportedException("Endpoint-pin writes are not supported against a remote gateway.");
}
