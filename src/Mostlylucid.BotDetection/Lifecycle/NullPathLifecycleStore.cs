namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Ephemeral-mode no-op: every recorded response is dropped, every lookup
///     returns null. The honeypot threat scorer sees "no lifecycle data" and
///     falls back to its baseline classification.
/// </summary>
public sealed class NullPathLifecycleStore : IPathLifecycleStore
{
    public Task RecordResponseAsync(string path, int statusCode, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default)
        => Task.FromResult<PathLifecycle?>(null);
}
