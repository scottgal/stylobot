namespace Mostlylucid.BotDetection.UI.Services.Routes;

public interface IRouteDiscoveryService
{
    IReadOnlyList<DiscoveredRoute> DiscoverRoutes();
}
