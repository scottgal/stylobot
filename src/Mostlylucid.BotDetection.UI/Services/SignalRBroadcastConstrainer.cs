using System.Collections.Concurrent;
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
/// </summary>
public static class SignalRBroadcastConstrainer
{
    private static readonly ConcurrentDictionary<string, byte> Pending = new();
    private static int _flushScheduled;

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
        Pending[signal] = 0;
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(intervalMs > 0 ? intervalMs : 10_000);
            try
            {
                // Snapshot, clear, emit. Any signal queued during the emit
                // becomes the seed of the next window.
                var signals = Pending.Keys.ToArray();
                foreach (var s in signals)
                    Pending.TryRemove(s, out _);
                foreach (var s in signals)
                    await hub.Clients.All.BroadcastInvalidation(s);
            }
            finally
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
            }
        });
    }
}
