using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR-backed <see cref="IFingerprintDirtyBroadcaster"/>. Wired automatically by
///     <c>AddStyloBotDashboard</c> so any host that mounts the dashboard surface gets
///     fingerprint-dirty beacons fan-out to every connected operator browser. Hosts
///     without the dashboard fall back to <see cref="NoOpFingerprintDirtyBroadcaster"/>.
///     <para>
///         Pairs with the commercial fingerprint-name editor endpoint and the HR1 Redis
///         subscriber: an operator rename on this gateway fires the local beacon directly;
///         a peer-gateway rename arriving over Redis fires the same beacon from the
///         subscriber handler so every dashboard in the fleet sees the row repaint
///         regardless of which gateway took the click.
///     </para>
///     <para>
///         Failures are swallowed: a transport hiccup on the hub must not turn into a
///         500 on the editor POST, and the durable Redis fan-out plus the local DB write
///         have already won by the time this beacon fires.
///     </para>
/// </summary>
public sealed class SignalRFingerprintDirtyBroadcaster : IFingerprintDirtyBroadcaster
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hub;

    public SignalRFingerprintDirtyBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hub)
    {
        _hub = hub;
    }

    /// <inheritdoc />
    public async Task PublishAsync(string fingerprintId, string slot, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        if (string.IsNullOrEmpty(slot)) return;
        try
        {
            await _hub.Clients.All.FingerprintDirty(fingerprintId, slot);
        }
        catch
        {
            // SignalR transport failures (no connected clients, dead socket mid-iteration,
            // disposed hub during shutdown) must not propagate -- the beacon is best-effort
            // dashboard polish over an already-persisted edit.
        }
    }
}