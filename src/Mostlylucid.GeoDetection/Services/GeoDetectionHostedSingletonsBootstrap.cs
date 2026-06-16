using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the GeoDetection assembly's migrated singletons at application start so
///     their constructors fire
///     <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator.Subscribe"/>
///     against the project-wide coordinator, and so the one-shot startup
///     download check runs deterministically before the first request.
///     <para>
///         Mirrors the
///         <c>Mostlylucid.BotDetection.Scheduling.BotDetectionHostedSingletonsBootstrap</c>,
///         <c>Mostlylucid.BotDetection.UI.Services.UiHostedSingletonsBootstrap</c>,
///         and <c>Mostlylucid.BotDetection.PrometheusPack.Extensions.PrometheusPackBootstrap</c>
///         patterns. Add new GeoDetection-assembly tick subscribers to the
///         resolve list in <see cref="StartAsync"/> as Wave 2 migrates them.
///     </para>
/// </summary>
internal sealed class GeoDetectionHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public GeoDetectionHostedSingletonsBootstrap(IServiceProvider services)
    {
        _services = services;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Force construction so the constructor runs Subscribe(...) against
        // the IScheduleCoordinator. GetService -- not GetRequiredService --
        // because a host that pares the DI graph may not register a
        // particular migrated singleton; the bootstrap should not crash the
        // host when the singleton is absent.
        var updater = _services.GetService<GeoLite2UpdateService>();
        if (updater is not null)
        {
            // Preserve the original BackgroundService behaviour of running the
            // "download on startup if file missing" check during host start
            // rather than waiting for the first hourly tick.
            await updater.EnsureStartupDownloadAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}