using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Periodic batch-flush for <see cref="SqlitePathLifecycleStore"/>. The
///     store's hot path is in-memory only; this service drains its dirty set
///     into SQLite every <see cref="FlushInterval"/> through the single
///     persistent connection.
/// </summary>
/// <remarks>
///     Replaces the per-request <c>new SqliteConnection</c> + UPSERT pattern
///     that ETW profiling identified as the dominant userland CPU cost (71%
///     of total under 200 RPS sustained load). Losing up to 30 seconds of
///     path counters on a crash is acceptable -- the next 2xx response on
///     the path re-marks <c>HasServed2xx</c> immediately, and the threat
///     scorer only consumes the two flip-bits (formerly-real,
///     first-4xx-after-2xx) not the absolute counts.
/// </remarks>
internal sealed class PathLifecycleFlushService : BackgroundService
{
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly SqlitePathLifecycleStore _store;
    private readonly ILogger<PathLifecycleFlushService> _logger;

    public PathLifecycleFlushService(
        IPathLifecycleStore store,
        ILogger<PathLifecycleFlushService> logger)
    {
        _store = store as SqlitePathLifecycleStore
                 ?? throw new InvalidOperationException(
                     $"PathLifecycleFlushService requires {nameof(SqlitePathLifecycleStore)} -- check DI registration");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await _store.FlushAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogDebug(ex, "PathLifecycle periodic flush failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>
    ///     Final drain on shutdown so in-flight dirties don't get lost when
    ///     the process exits cleanly.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await _store.FlushAsync(cancellationToken); }
        catch (Exception ex) { _logger.LogDebug(ex, "PathLifecycle shutdown flush failed"); }
        await base.StopAsync(cancellationToken);
    }
}
