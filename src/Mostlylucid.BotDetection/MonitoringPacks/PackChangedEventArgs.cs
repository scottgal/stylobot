namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class PackChangedEventArgs(IMonitoringPack pack) : EventArgs
{
    public IMonitoringPack Pack { get; } = pack;
}
