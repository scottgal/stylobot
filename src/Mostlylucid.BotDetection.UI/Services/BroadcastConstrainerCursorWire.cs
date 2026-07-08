using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Tiny <see cref="IHostedService"/> shim that wires the
///     <see cref="IDashboardChangeCursor"/> singleton into the process-static
///     <see cref="SignalRBroadcastConstrainer"/> so that each flush window can
///     stamp the <c>BroadcastDirty</c> beacon with the current monotonic tick
///     (Plan 3, Task 2).
///     <para>
///         Using a hosted service (rather than an inline call in the DI
///         registration lambda) defers the wiring to application start,
///         at which point the DI container is fully built and the cursor
///         singleton has been resolved.  No periodic loop, no
///         <c>BackgroundService</c> — just a one-shot <c>StartAsync</c>
///         per <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
internal sealed class BroadcastConstrainerCursorWire : IHostedService
{
    private readonly IDashboardChangeCursor _cursor;

    public BroadcastConstrainerCursorWire(IDashboardChangeCursor cursor)
        => _cursor = cursor;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SignalRBroadcastConstrainer.SetCursor(_cursor);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
