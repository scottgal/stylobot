namespace Mostlylucid.BotDetection.Data.Centroids;

/// <summary>
///     No-op <see cref="ICentroidWriter"/> for ephemeral / in-memory mode (CI, integration tests,
///     local dev). Drops every enqueued message silently; no SQLite connection required.
///     Registered via <c>TryAddSingleton</c> so the production
///     <see cref="SqliteCentroidWriter"/> wins when explicitly registered first.
/// </summary>
public sealed class NullCentroidWriter : ICentroidWriter
{
    public void Enqueue(CentroidWriteMessage message) { }
    public int QueueDepth => 0;
    public long DroppedCount => 0;
}
