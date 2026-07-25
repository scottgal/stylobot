using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Attributes;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Response-shape helpers for the dashboard REST endpoints. Every endpoint wraps its
///     payload in either <see cref="PaginatedResponse{T}"/> or <see cref="SingleResponse{T}"/>
///     and 503s when the backing store isn't registered - centralising both the
///     null-check + the envelope construction keeps each handler down to a single
///     call instead of the same 8-line boilerplate per surface.
/// </summary>
internal static class ApiEndpointHelpers
{
    /// <summary>
    ///     503 Service Unavailable with a standard "X not enabled" message. Returns
    ///     <see cref="ServiceUnavailableHttpResult"/> (not the framework's <c>ProblemHttpResult</c>)
    ///     so the OpenAPI generator can actually document the 503 - see that type for why.
    /// </summary>
    public static ServiceUnavailableHttpResult StoreUnavailable(string name)
        => ServiceUnavailableHttpResult.FromTitle($"{name} not enabled.");

    /// <summary>Wrap a list payload in <see cref="PaginatedResponse{T}"/>.</summary>
    public static Ok<PaginatedResponse<T>> Paginated<T>(IReadOnlyList<T> data, int offset, int limit)
        => TypedResults.Ok(new PaginatedResponse<T>
        {
            Data = data,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = data.Count },
            Meta = new ResponseMeta()
        });

    /// <summary>Wrap a list payload in <see cref="PaginatedResponse{T}"/> with offset = 0.</summary>
    public static Ok<PaginatedResponse<T>> Paginated<T>(IReadOnlyList<T> data, int limit)
        => Paginated(data, 0, limit);

    /// <summary>Wrap a scalar payload in <see cref="SingleResponse{T}"/>.</summary>
    public static Ok<SingleResponse<T>> Single<T>(T data)
        => TypedResults.Ok(new SingleResponse<T> { Data = data, Meta = new ResponseMeta() });

    /// <summary>
    ///     High-block-threshold bot policy for machine-to-machine /api/v1/* surfaces. Mirrors
    ///     the SignalR hub's BlockThreshold = 0.95 - API callers (stylobot-ui, sidecar clients,
    ///     external integrations) ARE bot-like by definition (no browser UA, no client-side
    ///     fingerprint, repeated polling), so the default block threshold would kill them
    ///     unfairly. The gateway still scores + records the request; only the action policy
    ///     is relaxed.
    /// </summary>
    public static RouteGroupBuilder WithApiBotPolicy(this RouteGroupBuilder group)
    {
        group.WithMetadata(new BotPolicyAttribute("default") { BlockThreshold = 0.95 });
        return group;
    }
}
