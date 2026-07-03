namespace Mostlylucid.BotDetection.Data;

public record PersistedRequest
{
    public long Id { get; init; }

    /// <summary>
    ///     Owner eTLD+1 of the request. Populated at the storage boundary from
    ///     <c>RequestScope.Domain</c>. Null on rows written before multi-domain
    ///     support landed.
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    ///     Full lowercased port-stripped Host header. Populated at the storage
    ///     boundary from <c>RequestScope.Host</c>. Null on rows written before
    ///     multi-domain support landed.
    /// </summary>
    public string? Host { get; init; }

    public required string Signature { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Path { get; init; }
    public required string MarkovState { get; init; }
    public required int StatusCode { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public required string RiskBand { get; init; }
    public required double ProcessingMs { get; init; }
    public long? SessionId { get; init; }
}
