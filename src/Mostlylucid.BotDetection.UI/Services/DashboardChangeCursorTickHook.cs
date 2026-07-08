using Microsoft.Extensions.Hosting;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Tiny <see cref="IHostedService"/> shim that subscribes
///     <see cref="DashboardChangeCursor.AdvanceTick"/> to
///     <see cref="TickCadence.Tick10s"/> on the project-wide
///     <see cref="IScheduleCoordinator"/>.
///     <para>
///         Using a hosted service for the Subscribe call (rather than inline
///         in the cursor's constructor) keeps <see cref="DashboardChangeCursor"/>
///         free of a required scheduler dependency — the cursor's constructor is
///         parameterless, which makes direct construction in unit tests trivial.
///         Per <c>feedback_no_background_services</c> this is NOT a
///         <c>BackgroundService</c>; it has no periodic loop of its own.
///     </para>
/// </summary>
internal sealed class DashboardChangeCursorTickHook : IHostedService
{
    private readonly DashboardChangeCursor _cursor;
    private readonly IScheduleCoordinator? _coordinator;
    private IDisposable? _subscription;

    public DashboardChangeCursorTickHook(
        IDashboardChangeCursor cursor,
        IScheduleCoordinator? coordinator = null)
    {
        // Cast is safe: the only registered impl is DashboardChangeCursor.
        _cursor = (DashboardChangeCursor)cursor;
        _coordinator = coordinator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_coordinator is not null)
        {
            _subscription = _coordinator.Subscribe(
                TickCadence.Tick10s,
                nameof(DashboardChangeCursor),
                CostHint.Low,
                (_, _) =>
                {
                    _cursor.AdvanceTick();
                    return Task.CompletedTask;
                });
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }
}
