using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     View model for the "Log sink" sub-row. Reflects current state of the
///     gateway log exporter / drainer: queue-depth gauge, configured batch
///     size + flush tick, posture (whether the gateway endpoint is reachable),
///     and the most recent log entries drawn from whatever LFU cache the gateway
///     already indexes logs into. <see cref="RecentEntries"/> is empty when
///     <see cref="IRecentLogEntriesProvider"/> isn't registered (FOSS host
///     without commercial OtelMesh wired).
/// </summary>
public sealed record LogSinkViewModel(
    bool LicensedAndEnabled,
    long QueueDepth,
    int BatchSize,
    string FlushTick,
    TimeSpan GatewayUnreachableAge,
    string GatewayEndpoint,
    IReadOnlyList<RecentLogEntry> RecentEntries);
