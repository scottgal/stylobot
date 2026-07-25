using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only surface for the entity-resolution layer. An entity is the durable
///     visitor handle that survives primary-signature rotation; multiple
///     primary signatures may bind to one entity via merge, one entity may split
///     into two on oscillation, and prior merges may be reverted. The dashboard
///     URLs against entity ids; this surface gives a remote-mode viewer everything
///     needed to render a <c>/dashboard/entity/{id}</c> page without local detection.
/// </summary>
public static class EntityEndpoints
{
    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/entities")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Entities")
            .WithApiBotPolicy();

        group.MapGet("/lookup/{primarySignature}", HandleLookup).WithName("LookupEntityId");
        group.MapGet("/{entityId}", HandleGet).WithName("GetEntity");
        group.MapGet("/{entityId}/edges", HandleEdges).WithName("GetEntityEdges");

        return endpoints;
    }

    /// <summary>
    ///     Resolve the entity id currently bound to <paramref name="primarySignature"/>.
    ///     Returns 404 when no edge maps this signature yet (first-encounter
    ///     window before the matcher allocates -- the caller should retry after
    ///     the request that triggers allocation completes).
    /// </summary>
    private static async Task<Results<Ok<SingleResponse<string>>, NotFound, ServiceUnavailableHttpResult>> HandleLookup(
        string primarySignature,
        [FromServices] IDetectionArchive? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        // ResolveEntityAsync allocates when missing. For a pure lookup we want
        // null-when-missing semantics, so use the read-only signature->entity
        // resolver instead.
        var entity = await store.GetEntityForSignatureAsync(primarySignature, ct);
        if (entity is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(entity.EntityId);
    }

    private static async Task<Results<Ok<SingleResponse<ResolvedEntity>>, NotFound, ServiceUnavailableHttpResult>> HandleGet(
        string entityId,
        [FromServices] IDetectionArchive? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        var entity = await store.GetEntityAsync(entityId, ct);
        if (entity is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(entity);
    }

    private static async Task<Results<Ok<PaginatedResponse<EntityEdge>>, ServiceUnavailableHttpResult>> HandleEdges(
        string entityId,
        [FromServices] IDetectionArchive? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        var edges = await store.GetEntityEdgesAsync(entityId, ct);
        return ApiEndpointHelpers.Paginated(edges, edges.Count);
    }
}
