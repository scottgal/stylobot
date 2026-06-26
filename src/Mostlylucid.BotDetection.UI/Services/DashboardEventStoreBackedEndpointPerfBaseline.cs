using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Read-through baseline cache backed by the FOSS dashboard event store.
///     On each <see cref="TickCadence.Tick1m"/> tick from
///     <c>IScheduleCoordinator</c>, pulls per-(method, path) stats from
///     <see cref="IDashboardEventStore.GetEndpointStatsAsync"/>, groups by
///     <c>(method, PathNormalizer.Normalize(path))</c>, computes a per-template
///     count-weighted p95 plus total sample count, and atomically swaps the
///     in-memory dictionary. <see cref="GetExpectedMs"/> is a lock-free read
///     against the snapshot.
///     <para>
///     Returns 0 for templates whose aggregated sample count is below
///     <see cref="PipelineLoadSensorOptions.MinSamplesForTrustedBaseline"/>
///     (strict less-than). Returns 0 on cache miss. Refresh failures preserve
///     the prior snapshot and emit a single warn log per failure (no spam).
///     </para>
/// </summary>
internal sealed class DashboardEventStoreBackedEndpointPerfBaseline : IEndpointPerfBaseline, IDisposable
{
    private readonly IDashboardEventStore _store;
    private readonly PipelineLoadSensorOptions _options;
    private readonly ILogger<DashboardEventStoreBackedEndpointPerfBaseline>? _logger;
    private readonly IDisposable? _subscription;

    // Atomic snapshot. Reads via Volatile.Read; writes via Interlocked.Exchange.
    private IReadOnlyDictionary<(string Method, string Template), double> _snapshot
        = new Dictionary<(string, string), double>();

    public DashboardEventStoreBackedEndpointPerfBaseline(
        IDashboardEventStore store,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null,
        ILogger<DashboardEventStoreBackedEndpointPerfBaseline>? logger = null)
    {
        _store = store;
        _options = options.Value.PipelineLoadSensor;
        _logger = logger;

        // Optional so test fixtures that construct the baseline directly (without
        // scheduling) keep working. Production DI passes the real coordinator.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m,
                "EndpointPerfBaseline",
                CostHint.Low,
                OnTickAsync);
        }
    }

    public double GetExpectedMs(string method, string normalizedPath)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(normalizedPath)) return 0.0;
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TryGetValue((method.ToUpperInvariant(), normalizedPath), out var p95) ? p95 : 0.0;
    }

    public void Dispose() => _subscription?.Dispose();

    private Task OnTickAsync(DateTimeOffset _, CancellationToken ct) => RefreshNowAsync(ct);

    /// <summary>
    ///     Test hook: run one refresh synchronously. Production callers use
    ///     the tick subscription.
    /// </summary>
    internal async Task RefreshNowAsync(CancellationToken ct)
    {
        try
        {
            var stats = await _store.GetEndpointStatsAsync(count: _options.MaxEndpointStatsRows);
            var grouped = new Dictionary<(string, string), (double WeightedP95Sum, long TotalCount)>();
            foreach (var s in stats)
            {
                if (string.IsNullOrEmpty(s.Method) || string.IsNullOrEmpty(s.Path)) continue;
                var template = PathNormalizer.Normalize(s.Path);
                var key = (s.Method.ToUpperInvariant(), template);
                grouped.TryGetValue(key, out var prior);
                grouped[key] = (
                    WeightedP95Sum: prior.WeightedP95Sum + s.P95ProcessingTimeMs * s.TotalCount,
                    TotalCount: prior.TotalCount + s.TotalCount);
            }
            var snapshot = new Dictionary<(string, string), double>(grouped.Count);
            foreach (var (key, agg) in grouped)
            {
                if (agg.TotalCount < _options.MinSamplesForTrustedBaseline) continue;
                snapshot[key] = agg.TotalCount > 0 ? agg.WeightedP95Sum / agg.TotalCount : 0.0;
            }
            Interlocked.Exchange(ref _snapshot, snapshot);
        }
        catch (Exception ex)
        {
            // Sampled warn (one per failure, no spam under sustained errors).
            // Prior snapshot stays in place so the hot path keeps reading
            // last-good values until the next successful refresh.
            _logger?.LogWarning(ex,
                "EndpointPerfBaseline refresh failed; keeping prior snapshot ({Count} templates)",
                Volatile.Read(ref _snapshot).Count);
        }
    }
}