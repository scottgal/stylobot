using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Process-wide rate limiter for outbound SignalR invalidation beacons.
///     <para>
///     Detection events fire at production traffic rates; without this every
///     event triggers a hub broadcast which then triggers every connected
///     dashboard to refetch every visible widget. The constrainer caps the
///     OUTBOUND rate: callers queue a signal name; the first call in a window
///     schedules a single fire-and-forget flush after <c>intervalMs</c>;
///     subsequent calls in that window add to the pending set; when the window
///     closes the flush emits each unique pending signal once. The client-side
///     debounce then collapses those into one batched /partials/update request.
///     </para>
///     <para>
///     One constrainer for the whole process, not one per signal name. Dashboard
///     invalidation channels are a small fixed set ("signature", "summary",
///     "threats", "clusters", ...); per-signal throttling would still produce
///     N beacons per window. The dashboard-level signal names here are NOT the
///     400-odd signal keys the detection pipeline emits per request.
///     </para>
///     <para>
///     <b>Plan 3 Task 2 — additive <c>BroadcastDirty</c> beacon.</b>
///     On each flush, in addition to the existing per-surface
///     <see cref="IStyloBotDashboardHub.BroadcastInvalidation"/> calls (back-compat),
///     the constrainer emits ONE structured
///     <see cref="IStyloBotDashboardHub.BroadcastDirty"/> carrying the tick from
///     <see cref="IDashboardChangeCursor.CurrentTick"/> and the full set of surfaces
///     flushed in that window. Clients that have subscribed to
///     <c>BroadcastDirty</c> use the tick to skip widgets already at the current
///     version. Clients that only subscribe to <c>BroadcastInvalidation</c> are
///     completely unaffected — that path is unchanged.
///     </para>
///     <para>
///     <b>Static + DI seam:</b> the cursor is optional. Call
///     <see cref="SetCursor"/> once at DI registration time (or in tests) to
///     wire the tick source. If the cursor is null (not wired) the
///     <c>BroadcastDirty</c> beacon is skipped; all existing behaviour is
///     preserved.
///     </para>
/// </summary>
public static class SignalRBroadcastConstrainer
{
    // Per-hub flush window. Production has exactly ONE hub context (the SignalR
    // IHubContext singleton), so this table holds a single entry and behaves
    // identically to the previous process-global Pending + _flushScheduled. The
    // per-hub keying exists so tests — which create multiple hub contexts — get
    // ISOLATED flush windows: without it, concurrent tests raced the single global
    // _flushScheduled gate and the loser's signals emitted to the winner's hub,
    // so the loser's hub never received its beacon (the BroadcastDirtyTests flake).
    private sealed class FlushWindow
    {
        public readonly ConcurrentDictionary<string, byte> Pending = new();
        public int FlushScheduled;
        // Cursor captured when THIS window opens, so a later SetCursor (another
        // test, or a host starting) can't change the tick this window reports.
        public IDashboardChangeCursor? Cursor;
    }

    // ConditionalWeakTable: a window's state is collected with its hub context, so
    // transient test hub contexts don't leak. Keyed on the hub context reference.
    private static readonly ConditionalWeakTable<object, FlushWindow> Windows = new();

    // Optional cursor for tick-versioned BroadcastDirty. Null when not wired
    // (e.g. tests that only care about BroadcastInvalidation, or hosts where
    // DI hasn't registered IDashboardChangeCursor). Thread-safe via volatile
    // read/write: the reference is set once at startup and never mutated again.
    private static volatile IDashboardChangeCursor? _cursor;

    /// <summary>
    ///     Wires the cursor used for the <c>BroadcastDirty</c> tick payload.
    ///     Call once at DI registration / application start. Passing
    ///     <c>null</c> clears the cursor (test teardown).
    /// </summary>
    public static void SetCursor(IDashboardChangeCursor? cursor)
        => _cursor = cursor;

    /// <summary>
    ///     Queue a signal for broadcast. Returns immediately. The actual hub
    ///     emit happens (at most once per <paramref name="intervalMs"/>) on a
    ///     background task.
    /// </summary>
    public static void Queue(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hub,
        string signal,
        int intervalMs)
    {
        var window = Windows.GetOrCreateValue(hub);
        window.Pending[signal] = 0;
        if (Interlocked.CompareExchange(ref window.FlushScheduled, 1, 0) != 0) return;

        // Capture the cursor for THIS window at open time (see FlushWindow.Cursor).
        window.Cursor = _cursor;

        _ = Task.Run(async () =>
        {
            await Task.Delay(intervalMs > 0 ? intervalMs : 10_000);
            try
            {
                // Snapshot, clear, emit. Any signal queued during the emit
                // becomes the seed of the next window.
                var signals = window.Pending.Keys.ToArray();
                foreach (var s in signals)
                    window.Pending.TryRemove(s, out _);

                // Back-compat: per-surface BroadcastInvalidation (unchanged).
                foreach (var s in signals)
                    await hub.Clients.All.BroadcastInvalidation(s);

                // Plan 3 Task 2: ONE structured BroadcastDirty per flush window
                // alongside the existing per-surface invalidations (additive).
                // Skipped gracefully when cursor is not wired.
                var cursor = window.Cursor;
                if (cursor is not null && signals.Length > 0)
                {
                    var beacon = new DashboardDirtyBeacon(cursor.CurrentTick, signals);
                    await hub.Clients.All.BroadcastDirty(beacon);
                }
            }
            finally
            {
                Interlocked.Exchange(ref window.FlushScheduled, 0);
            }
        });
    }
}
