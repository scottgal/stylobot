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
}
