using Microsoft.Extensions.Hosting;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Atoms;

public sealed class PolicyStackHitAtomTickHook : IHostedService, IDisposable
{
    private readonly IScheduleCoordinator _scheduler;
    private readonly PolicyStackHitAtom _atom;
    private IDisposable? _subscription;

    public PolicyStackHitAtomTickHook(IScheduleCoordinator scheduler, PolicyStackHitAtom atom)
    {
        _scheduler = scheduler;
        _atom = atom;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _scheduler.Subscribe(
            TickCadence.Tick1m,
            nameof(PolicyStackHitAtom),
            CostHint.Low,
            (_, _) => { _atom.AgeOut(); return Task.CompletedTask; });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}