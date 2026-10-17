namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     Persists operator-assigned friendly names for discovered routes. Survives restarts
///     by living in the dashboard SQLite database alongside detections and metric snapshots.
/// </summary>
public interface IRouteNameStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<RouteNameEntry?> GetAsync(string routeKey, CancellationToken ct = default);
    Task<IReadOnlyList<RouteNameEntry>> GetAllAsync(CancellationToken ct = default);
    Task SetAsync(string routeKey, string friendlyName, string? notes, string? updatedBy, CancellationToken ct = default);
    Task RemoveAsync(string routeKey, CancellationToken ct = default);
}
