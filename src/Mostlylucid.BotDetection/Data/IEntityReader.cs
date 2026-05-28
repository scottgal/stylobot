namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Read-only slice of the entity-resolution layer consumed by the dashboard
///     (entity-detail route, /dashboard/entity/{id}; URL generation; cache
///     keying). Split out so a remote-mode dashboard host can satisfy these
///     reads over the gateway's <c>/api/v1/entities/*</c> surface without
///     dragging in <see cref="ISessionStore"/>'s write methods. Mirrors the
///     <see cref="Identity.IFingerprintReader"/> / <c>SqliteFingerprintStore</c>
///     split: one focused interface, two impls (local wraps ISessionStore;
///     remote proxies the REST endpoints).
/// </summary>
public interface IEntityReader
{
    /// <summary>
    ///     Resolve the entity id currently bound to <paramref name="primarySignature"/>,
    ///     or null when no edge maps this signature yet (first-encounter window
    ///     before the matcher allocates). Read-only -- does NOT allocate when
    ///     missing; the matcher's request-time path
    ///     (StyloBotForwardedHeadersMiddleware) owns allocation.
    /// </summary>
    Task<string?> LookupEntityIdAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>
    ///     Fetch the entity record by id. Null when unknown.
    /// </summary>
    Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    ///     All edges (signature->entity bindings) for one entity, in insertion
    ///     order. Used by the entity-detail page to enumerate every primary
    ///     signature bound to this actor and to show merge history. Includes
    ///     reverted edges (audit trail); callers filter on <see cref="EntityEdge.IsActive"/>
    ///     when they only want the current resolution.
    /// </summary>
    Task<IReadOnlyList<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default);
}

/// <summary>
///     Local <see cref="IEntityReader"/> implementation: thin pass-through to the
///     in-process <see cref="ISessionStore"/>. Registered by
///     <c>AddBotDetection</c> when a session store is available; a remote-mode
///     dashboard host overrides this with <c>RemoteEntityReader</c> in
///     <c>AddStyloBotDashboardRemote</c>.
/// </summary>
internal sealed class LocalEntityReader : IEntityReader
{
    private readonly ISessionStore _store;

    public LocalEntityReader(ISessionStore store) => _store = store;

    public async Task<string?> LookupEntityIdAsync(string primarySignature, CancellationToken ct = default)
    {
        var entity = await _store.GetEntityForSignatureAsync(primarySignature, ct);
        return entity?.EntityId;
    }

    public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
        => _store.GetEntityAsync(entityId, ct);

    public async Task<IReadOnlyList<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
        => await _store.GetEntityEdgesAsync(entityId, ct);
}
