namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     Default no-op controller registered by FOSS. Reports no hot-reload support for
///     any pack and rejects all replace attempts. The event is exposed for interface
///     compliance but is never raised.
/// </summary>
public sealed class NullPackRuntimeController : IPackRuntimeController
{
    public bool SupportsHotReload(string packId) => false;

    public Task ReplacePackAsync(IMonitoringPack pack, CancellationToken ct)
        => throw new NotSupportedException(
            $"Hot reload of pack '{pack.Id}' requires the commercial monitoring pack with a valid per-pack capability claim.");

    public Task ReloadAllAsync(CancellationToken ct) => Task.CompletedTask;

    public event EventHandler<PackChangedEventArgs>? PackChanged
    {
        add { /* no-op: never raised */ }
        remove { /* no-op */ }
    }
}
