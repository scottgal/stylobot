using System.Collections.Concurrent;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Per-surface tick/version for the dashboard update flow (Plan 3, Task 1).
///     Tracks the monotonic tick at which each dashboard surface last changed
///     so downstream tasks can skip widgets that are already at the current tick.
/// </summary>
public interface IDashboardChangeCursor
{
    /// <summary>
    ///     Monotonic counter starting at 1. Advanced once per
    ///     <see cref="TickCadence.Tick10s"/> via <see cref="DashboardChangeCursor.AdvanceTick"/>.
    /// </summary>
    long CurrentTick { get; }

    /// <summary>Records that <paramref name="surface"/> changed at <see cref="CurrentTick"/>.</summary>
    void Bump(string surface);

    /// <summary>
    ///     The tick at which <paramref name="surface"/> last changed.
    ///     Returns 0 if the surface has never been bumped.
    /// </summary>
    long TickFor(string surface);

    /// <summary>
    ///     All surfaces whose <see cref="TickFor"/> equals <see cref="CurrentTick"/>
    ///     (i.e. changed since the last tick advance).
    /// </summary>
    IReadOnlyList<string> SurfacesChangedThisTick();
}

/// <summary>
///     Default singleton implementation of <see cref="IDashboardChangeCursor"/>.
///     <para>
///         <b>Tick source:</b> <see cref="AdvanceTick"/> is called by a
///         <see cref="IScheduleCoordinator"/> <see cref="TickCadence.Tick10s"/>
///         subscription wired up in <see cref="DashboardChangeCursorTickHook"/> (a
///         tiny <see cref="Microsoft.Extensions.Hosting.IHostedService"/> shim that
///         does the Subscribe call). No <c>BackgroundService</c> used per
///         <c>feedback_no_background_services</c>.
///     </para>
///     <para>
///         <b>Test-friendliness:</b> <see cref="AdvanceTick"/> is <c>public</c> so
///         unit tests can drive the counter directly without a real scheduler.
///     </para>
/// </summary>
public sealed class DashboardChangeCursor : IDashboardChangeCursor
{
    private long _currentTick = 1;
    private readonly ConcurrentDictionary<string, long> _lastChange = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public long CurrentTick => Interlocked.Read(ref _currentTick);

    /// <inheritdoc />
    public void Bump(string surface)
    {
        if (string.IsNullOrEmpty(surface)) return;
        _lastChange[surface] = CurrentTick;
    }

    /// <inheritdoc />
    public long TickFor(string surface) =>
        _lastChange.TryGetValue(surface, out var tick) ? tick : 0L;

    /// <inheritdoc />
    public IReadOnlyList<string> SurfacesChangedThisTick()
    {
        var now = CurrentTick;
        return _lastChange
            .Where(kv => kv.Value == now)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    ///     Advances the monotonic tick counter by one.
    ///     Called from the <see cref="IScheduleCoordinator"/> <see cref="TickCadence.Tick10s"/>
    ///     subscription; also called directly in unit tests.
    /// </summary>
    public void AdvanceTick() => Interlocked.Increment(ref _currentTick);
}
