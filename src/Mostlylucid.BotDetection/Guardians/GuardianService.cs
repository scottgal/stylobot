using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Walks every registered <see cref="IGuardian"/> on its own
///     <see cref="IGuardian.Interval"/> and keeps the latest
///     <see cref="GuardianReport"/> per guardian for the dashboard roster + live
///     count. FOSS-core; categories (Data / Compliance / License) share this one
///     walker.
///     <para>
///         Not a <c>BackgroundService</c> (see <c>feedback_no_background_services</c>):
///         subscribes to <see cref="TickCadence.Tick1m"/> on
///         <see cref="IScheduleCoordinator"/> and gates per guardian via a
///         <c>nextRun</c> map, so a 6 h and a 20 min guardian coexist without
///         everyone running every beat. A throwing guardian is backed off 5 min
///         and never takes the walker (or its peers) down.
///     </para>
/// </summary>
public sealed class GuardianService : IDisposable
{
    private static readonly TimeSpan BackoffOnError = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyList<IGuardian> _guardians;
    private readonly ILogger<GuardianService> _logger;
    private readonly Dictionary<string, DateTime> _nextRun;
    private readonly ConcurrentDictionary<string, GuardianReport> _latest = new();
    private readonly IDisposable? _subscription;
    private int _disposed;

    public GuardianService(
        IEnumerable<IGuardian> guardians,
        ILogger<GuardianService> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _guardians = guardians.ToList();
        _logger = logger;
        // Every guardian is due immediately so the first tick runs one pass each.
        _nextRun = _guardians.ToDictionary(g => g.Name, _ => DateTime.UtcNow);

        // Optional so unit tests can drive RunDueAsync directly without a coordinator.
        if (scheduleCoordinator is not null && _guardians.Count > 0)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m, nameof(GuardianService), CostHint.Medium, OnTickAsync);
            _logger.LogInformation(
                "GuardianService subscribed to Tick1m with {Count} guardians", _guardians.Count);
        }
    }

    /// <summary>Registered guardians (for the live count).</summary>
    public IReadOnlyList<IGuardian> Guardians => _guardians;

    /// <summary>Latest report per guardian name (empty until each has run once).</summary>
    public IReadOnlyDictionary<string, GuardianReport> LatestReports => _latest;

    /// <summary>Coordinator tick handler; public so tests drive one beat deterministically.</summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct) =>
        await RunDueAsync(now, ct);

    /// <summary>Run every guardian whose interval has elapsed by <paramref name="now"/>.</summary>
    public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_disposed != 0) return;
        var nowUtc = now.UtcDateTime;

        foreach (var g in _guardians)
        {
            if (ct.IsCancellationRequested) return;
            if (!g.Enabled) continue;
            if (nowUtc < _nextRun[g.Name]) continue;

            var sw = Stopwatch.StartNew();
            try
            {
                var report = await g.GuardAsync(ct) with { At = now };
                _latest[g.Name] = report;
                _nextRun[g.Name] = nowUtc.Add(g.Interval);
                if (report.Status != "ok")
                    _logger.LogInformation(
                        "Guardian {Name}: {Status} rows {Before}->{After}, {Bytes} B reclaimed in {Ms:F0}ms",
                        g.Name, report.Status, report.RowsBefore, report.RowsAfter,
                        report.BytesReclaimed, report.DurationMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Guardian {Name} threw; backing off {Backoff}", g.Name, BackoffOnError);
                _latest[g.Name] = new GuardianReport
                {
                    GuardianName = g.Name, Category = g.Category, Status = "error",
                    Details = ex.Message, DurationMs = sw.Elapsed.TotalMilliseconds, At = now
                };
                _nextRun[g.Name] = nowUtc.Add(BackoffOnError);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); } catch { /* coordinator already torn down */ }
    }
}
