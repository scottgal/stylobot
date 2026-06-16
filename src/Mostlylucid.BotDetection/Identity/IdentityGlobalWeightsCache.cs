using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     In-process cache for the calibrated global per-dimension weight vector. The matcher reads
///     it on every request to compose with the per-fingerprint weights at confirm + Pass 2 time;
///     a background refresh on the cadence configured by <see cref="IdentityWeightsOptions.GlobalRefreshSeconds"/>
///     pulls the latest row from <see cref="IFingerprintStore.GetGlobalWeightsAsync"/>.
///
///     Until calibration has run, <see cref="Current"/> returns null and
///     <see cref="Compose"/> returns the per-fingerprint vector unchanged — global weights are
///     a refinement, not a prerequisite.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> is false.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(GlobalRefreshSeconds)</c> loop (default 60 s); now
///         subscribes to <see cref="TickCadence.Tick10s"/> and gates each
///         <see cref="RefreshAsync"/> pass on "last-success older than
///         <see cref="IdentityWeightsOptions.GlobalRefreshSeconds"/>" so the
///         configured cadence is preserved. The startup "prime once on first
///         construction" semantics are kept by treating the first tick as the
///         priming refresh (<c>_lastRefreshedAtUtc == MinValue</c> always fires).
///     </para>
/// </summary>
public sealed class IdentityGlobalWeightsCache : IDisposable
{
    private readonly ILogger<IdentityGlobalWeightsCache> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly IDisposable? _subscription;
    private float[]? _current;
    private DateTime _lastRefreshedAt = DateTime.MinValue;
    private DateTime _lastRefreshAttemptUtc = DateTime.MinValue;
    private int _disposed;

    public IdentityGlobalWeightsCache(
        ILogger<IdentityGlobalWeightsCache> logger,
        IFingerprintStore store,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _store = store;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;

        // Optional so test fixtures that construct the cache directly (without
        // scheduling) keep working. Production DI passes the real coordinator.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "IdentityGlobalWeightsCache",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>The latest cached global weight vector, or null if calibration hasn't run.</summary>
    public float[]? Current => Volatile.Read(ref _current);

    /// <summary>Timestamp of the last successful refresh; <see cref="DateTime.MinValue"/> until first refresh.</summary>
    public DateTime LastRefreshedAt => _lastRefreshedAt;

    /// <summary>
    ///     Multiplicatively compose the global weight vector (if loaded) with the supplied
    ///     per-fingerprint weights. The result has the same length as the per-fp vector. When
    ///     global weights are absent or shape-mismatched, returns the per-fp vector unchanged.
    /// </summary>
    public float[] Compose(float[] perFpWeights)
    {
        var global = Volatile.Read(ref _current);
        if (global is null || global.Length != perFpWeights.Length) return perFpWeights;
        var composed = new float[perFpWeights.Length];
        for (var i = 0; i < perFpWeights.Length; i++)
            composed[i] = perFpWeights[i] * global[i];
        return composed;
    }

    /// <summary>
    ///     One-shot refresh. Pulls the latest row from the store and atomically swaps the cached
    ///     vector. Safe to call concurrently; readers see either the old or the new vector, never
    ///     a torn state. Returns true when a fresh vector was loaded.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken ct)
    {
        try
        {
            var row = await _store.GetGlobalWeightsAsync(ct);
            if (row is null) return false;
            Volatile.Write(ref _current, row.Value.Weights);
            _lastRefreshedAt = row.Value.LastComputedAt;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Global weights refresh failed");
            return false;
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick10s; gates the
    ///     underlying refresh on the configured GlobalRefreshSeconds so a
    ///     long-cadence configuration (e.g. 5 minutes) throttles accordingly,
    ///     while a short cadence (e.g. 5 s) fires roughly every tick. Dormant
    ///     when Identity.Enabled is false. Public so tests can drive a single
    ///     beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_enabled) return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Weights.GlobalRefreshSeconds));
        if (_lastRefreshAttemptUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastRefreshAttemptUtc < interval)
        {
            return; // Not yet due.
        }

        _lastRefreshAttemptUtc = now.UtcDateTime;
        await RefreshAsync(ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
