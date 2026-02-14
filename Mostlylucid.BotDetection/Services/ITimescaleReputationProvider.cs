namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Provides cached 90-day historical reputation data from TimescaleDB.
///     Implemented in the PostgreSQL persistence layer.
/// </summary>
public interface ITimescaleReputationProvider
{
    /// <summary>
    ///     Get historical reputation for a signature. Returns null if no data.
    ///     Results are cached per-signature with a 5-minute TTL.
    /// </summary>
    Task<TimescaleReputationData?> GetReputationAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>
    ///     Invalidate the cached reputation for a signature (e.g. after LLM classification).
    /// </summary>
    void InvalidateCache(string primarySignature);
}
