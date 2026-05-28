namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Sqlite-free no-op pinned endpoint store. Pinning is an operator
///     write-side feature; commercial gateways register this until the
///     Postgres-backed pinned endpoint store lands. Returns empty / drops
///     writes -- the EndpointPolicy resolver's per-request lookup harmlessly
///     reads "no pins" and falls through to normal classification.
/// </summary>
public sealed class NullPinnedEndpointStore : IPinnedEndpointStore
{
    public Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PinnedEndpoint>>(Array.Empty<PinnedEndpoint>());

    public Task<PinnedEndpoint?> AddAsync(string method, string path, bool isHoneypot, string? note, CancellationToken ct = default)
        => Task.FromResult<PinnedEndpoint?>(null);

    public Task<bool> RemoveAsync(long id, CancellationToken ct = default)
        => Task.FromResult(false);
}
