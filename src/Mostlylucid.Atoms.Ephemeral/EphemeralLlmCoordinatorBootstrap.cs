using Microsoft.Extensions.Hosting;

namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     IHostedService whose only job is to keep an
///     <see cref="EphemeralLlmCoordinator{TItem,TResult}"/> rooted at startup
///     so its constructor's <c>Subscribe</c> call lands on the schedule. The
///     coordinator does its own work via tick callbacks — this class has no
///     loop, no Start logic, no Stop logic beyond Dispose.
/// </summary>
public sealed class EphemeralLlmCoordinatorBootstrap<TItem, TResult> : IHostedService
{
    private readonly EphemeralLlmCoordinator<TItem, TResult> _coordinator;

    public EphemeralLlmCoordinatorBootstrap(EphemeralLlmCoordinator<TItem, TResult> coordinator)
        => _coordinator = coordinator;

    public Task StartAsync(CancellationToken _) => Task.CompletedTask;
    public Task StopAsync(CancellationToken _)
    {
        _coordinator.Dispose();
        return Task.CompletedTask;
    }
}
