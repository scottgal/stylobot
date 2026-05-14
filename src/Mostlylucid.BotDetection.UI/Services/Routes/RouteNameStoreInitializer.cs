using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     Calls <see cref="IRouteNameStore.InitializeAsync"/> at host startup so the
///     route_names table exists before any read or write. Idempotent.
/// </summary>
internal sealed class RouteNameStoreInitializer(IRouteNameStore store) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => store.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
