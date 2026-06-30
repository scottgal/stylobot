using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Eagerly resolves <see cref="DegradationStoreSampler"/> at host start
///     so its constructor runs the <c>IScheduleCoordinator.Subscribe</c> call
///     and the sampler starts persisting <see cref="Mostlylucid.BotDetection.RateLimit.DegradationSnapshot"/>
///     rows on the first <c>Tick10s</c>. DI singletons are otherwise lazy;
///     without this shim the subscription would only wire up after a request
///     touched the surface that depends on it.
///     <para>
///         Mirrors the <c>BotDetectionHostedSingletonsBootstrap</c> pattern
///         in the FOSS core (which lives in <c>Mostlylucid.BotDetection</c>
///         and cannot reference UI). Kept narrowly scoped to one singleton
///         so it doesn't grow into a kitchen-sink hosted service.
///     </para>
/// </summary>
internal sealed class DegradationStoreSamplerBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public DegradationStoreSamplerBootstrap(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // GetService -- not GetRequiredService -- because hosts that pare
        // the dashboard out of DI (e.g. a detection-only sidecar) should
        // not crash on boot when the sampler is absent.
        _services.GetService<DegradationStoreSampler>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
