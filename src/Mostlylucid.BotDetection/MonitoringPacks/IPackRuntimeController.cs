namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     Optional FOSS extension point for hot-swapping monitoring pack configuration
///     at runtime without restarting the host. The default <see cref="NullPackRuntimeController"/>
///     reports no support and rejects all writes; commercial implementations override this
///     behavior with per-pack license-gated reloads.
///
///     FOSS code (e.g. <c>MeterListenerService</c>) subscribes to <see cref="PackChanged"/>
///     to rebuild listener subscriptions. It never inspects license state; that is the
///     commercial controller's responsibility.
/// </summary>
public interface IPackRuntimeController
{
    /// <summary>
    ///     True when the named pack can be hot-reloaded. The result is per-pack so a
    ///     license authorising one pack does not unlock others.
    /// </summary>
    bool SupportsHotReload(string packId);

    /// <summary>
    ///     Replace the configuration of a single registered pack. Implementations must
    ///     validate the caller's capability against the license at every call (defence
    ///     in depth - registration-time checks alone are not sufficient).
    /// </summary>
    Task ReplacePackAsync(IMonitoringPack pack, CancellationToken ct);

    /// <summary>
    ///     Re-emit <see cref="PackChanged"/> for every registered pack. Used after bulk
    ///     config restore or license refresh.
    /// </summary>
    Task ReloadAllAsync(CancellationToken ct);

    /// <summary>
    ///     Fired after a pack's configuration has been replaced. Subscribers (e.g.
    ///     <c>MeterListenerService</c>) should rebuild any state derived from the pack.
    /// </summary>
    event EventHandler<PackChangedEventArgs>? PackChanged;
}
