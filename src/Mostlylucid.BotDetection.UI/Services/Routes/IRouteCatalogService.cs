namespace Mostlylucid.BotDetection.UI.Services.Routes;

public interface IRouteCatalogService
{
    /// <summary>
    ///     Returns every route the host has registered, joined with any operator-assigned
    ///     friendly names + notes from <see cref="IRouteNameStore"/>. Sorted by route key.
    /// </summary>
    Task<IReadOnlyList<RouteCatalogEntry>> GetCatalogAsync(CancellationToken ct = default);
}
