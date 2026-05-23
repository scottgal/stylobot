namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Tracks the response history of each request path. Read by the honeypot
///     threat scorer to decide whether a 4xx hit is "scanner probing a path
///     that never existed" (low threat) or "scanner probing a path that used
///     to be real" (high threat -- they remember).
/// </summary>
public interface IPathLifecycleStore
{
    /// <summary>
    ///     Record a single response observation. Cheap, idempotent, MUST NOT
    ///     block the request pipeline -- the response-side middleware
    ///     fire-and-forgets this call. Static asset paths (CSS/JS/images
    ///     and fonts) are filtered out at the call site.
    /// </summary>
    Task RecordResponseAsync(string path, int statusCode, CancellationToken ct = default);

    /// <summary>
    ///     Look up the lifecycle for a path. Returns null when the path has
    ///     never been observed. Hot path -- result is read on every honeypot
    ///     classification, so implementations should keep an in-memory cache.
    /// </summary>
    Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default);
}
