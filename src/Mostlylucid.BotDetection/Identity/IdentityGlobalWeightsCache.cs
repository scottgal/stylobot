using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

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
/// </summary>
public sealed class IdentityGlobalWeightsCache : BackgroundService
{
    private readonly ILogger<IdentityGlobalWeightsCache> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private float[]? _current;
    private DateTime _lastRefreshedAt = DateTime.MinValue;

    public IdentityGlobalWeightsCache(
        ILogger<IdentityGlobalWeightsCache> logger,
        IFingerprintStore store,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("IdentityGlobalWeightsCache dormant: Identity.Enabled = false");
            return;
        }

        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.Weights.GlobalRefreshSeconds));

        // Prime once on startup so the matcher doesn't have to wait a full cycle for the first
        // load when the calibration row already exists from a prior run.
        await RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
            await RefreshAsync(stoppingToken);
        }
    }
}
