using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only <see cref="IRouteNameStore"/> for the remote (viewer) dashboard. The FOSS
///     dashboard is view-only: friendly route names are config-driven on the gateway and
///     surfaced through the gateway catalog (<c>GET /api/v1/routes</c>), so the viewer just
///     reads them. There is no local persistence (no SQLite file) and no live write path --
///     naming/pinning is a gateway-side config concern, never a runtime operation in the
///     viewer. Writes throw <see cref="NotSupportedException"/> rather than silently no-op so
///     a stray write attempt is visible instead of pretending to succeed.
/// </summary>
internal sealed class RemoteRouteNameStore : IRouteNameStore
{
    private readonly GatewayApiClient _api;

    public RemoteRouteNameStore(GatewayApiClient api) => _api = api;

    // Nothing to initialise: no local store, no schema. Pure read over the gateway API.
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<RouteNameEntry?> GetAsync(string routeKey, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(e => string.Equals(e.RouteKey, routeKey, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<RouteNameEntry>> GetAllAsync(CancellationToken ct = default)
    {
        // The gateway catalog already carries the friendly name on each entry; surface only
        // the routes that actually have one (the rest render from discovery/OpenAPI).
        var entries = await _api.GetEnvelopeListAsync<RemoteRouteEntry>("/api/v1/routes", ct);
        return entries
            .Where(e => !string.IsNullOrEmpty(e.RouteKey) && !string.IsNullOrEmpty(e.FriendlyName))
            .Select(e => new RouteNameEntry(e.RouteKey!, e.FriendlyName!, e.Notes, DateTime.UtcNow, null))
            .ToList();
    }

    public Task SetAsync(string routeKey, string friendlyName, string? notes, string? updatedBy, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Route naming is config-driven and never a live write in the view-only dashboard.");

    public Task RemoveAsync(string routeKey, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Route naming is config-driven and never a live write in the view-only dashboard.");

    /// <summary>Minimal projection of the gateway's route-catalog entry (reflection JSON, web defaults).</summary>
    private sealed record RemoteRouteEntry(string? RouteKey, string? FriendlyName, string? Notes);
}