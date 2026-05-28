namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     Sqlite-free no-op route name store. Route name mappings are an
///     operator-set admin surface; commercial gateways register this so
///     the FOSS Sqlite TryAdd never wins. Reads return null/empty -- the
///     dashboard falls back to the raw route key.
/// </summary>
public sealed class NullRouteNameStore : IRouteNameStore
{
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<RouteNameEntry?> GetAsync(string routeKey, CancellationToken ct = default)
        => Task.FromResult<RouteNameEntry?>(null);

    public Task<IReadOnlyList<RouteNameEntry>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RouteNameEntry>>(Array.Empty<RouteNameEntry>());

    public Task SetAsync(string routeKey, string friendlyName, string? notes, string? updatedBy, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RemoveAsync(string routeKey, CancellationToken ct = default)
        => Task.CompletedTask;
}
