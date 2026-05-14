using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.OpenApi;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbRoutesTabViewComponent(
    IRouteCatalogService catalog,
    IOpenApiCatalog openApi,
    StyloBotDashboardOptions options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var entries = await catalog.GetCatalogAsync();
        return View(new RoutesTabModel
        {
            Entries = entries,
            BasePath = options.BasePath.TrimEnd('/'),
            OpenApiSourceCount = openApi.Documents.Count,
            OpenApiOperationCount = openApi.OperationCount,
            OpenApiIsSeeded = openApi.IsSeeded
        });
    }
}

public sealed class RoutesTabModel
{
    public required IReadOnlyList<RouteCatalogEntry> Entries { get; init; }
    public required string BasePath { get; init; }
    public int OpenApiSourceCount { get; init; }
    public int OpenApiOperationCount { get; init; }
    public bool OpenApiIsSeeded { get; init; }

    public int DiscoveredCount => Entries.Count(e => e.IsDiscovered);
    public int DocumentedCount => Entries.Count(e => e.IsDocumented);
    public int PhantomCount => Entries.Count(e => e.IsDocumented && !e.IsDiscovered);
    public int UndocumentedCount => Entries.Count(e => e.IsDiscovered && !e.IsDocumented);
}
