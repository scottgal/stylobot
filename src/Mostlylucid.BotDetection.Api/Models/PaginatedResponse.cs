namespace Mostlylucid.BotDetection.Api.Models;

public sealed record PaginatedResponse<T>
{
    public required IReadOnlyList<T> Data { get; init; }
    public required PaginationInfo Pagination { get; init; }
    public required ResponseMeta Meta { get; init; }
}

public sealed record SingleResponse<T>
{
    public required T Data { get; init; }
    public required ResponseMeta Meta { get; init; }
}

public sealed record PaginationInfo
{
    public required int Offset { get; init; }
    public required int Limit { get; init; }
    public required int Total { get; init; }
}

public sealed record ResponseMeta
{
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     For <c>GET /api/v1/endpoints</c>: <c>windowed</c> when the backing store can
    ///     answer the requested time range, <c>lifetime</c> when it only has lifetime
    ///     counters. Null on every other route and on older gateways. A remote viewer
    ///     must treat null as unknown (conservatively: not windowed), never as windowed.
    /// </summary>
    public string? EndpointStatsWindow { get; init; }
}

/// <summary>
///     Typed error payload for endpoints that previously returned anonymous
///     <c>new { error = "..." }</c> objects. Anonymous types can't be source-generated,
///     so the typed form is what makes the endpoints AOT-compatible.
/// </summary>
public sealed record ApiError(string Error);
