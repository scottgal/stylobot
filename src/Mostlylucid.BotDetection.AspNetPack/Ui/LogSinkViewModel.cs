namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     View model for the "Log sink" sub-row. Reflects current state of the
///     gateway log exporter / drainer: queue-depth gauge, configured batch
///     size + flush tick, and posture (whether the gateway endpoint is
///     reachable).
/// </summary>
public sealed record LogSinkViewModel(
    bool LicensedAndEnabled,
    long QueueDepth,
    int BatchSize,
    string FlushTick,
    TimeSpan GatewayUnreachableAge,
    string GatewayEndpoint);
