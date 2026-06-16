using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Composite-spec step 4: recomputes the parent fingerprint's centroid as
///     the maturity-weighted mean of its child mode centroids on a fixed tick.
///     Per the architecture doc the parent centroid is the rollup of the per-
///     mode evolution -- Pass 2 + index search read it as before, but its
///     content is now derived from the per-mode learning that landed in steps
///     3 and 3.5.
///
///     Gated by <see cref="BrowserModeOptions.RollupEnabled"/> (default false).
///     Until an operator flips it on, the service still runs but skips the
///     rollup write so the parent absorption path stays the sole owner of the
///     centroid. This makes the rollup math observable on staging via the
///     <c>TickOnceAsync(dryRun: true)</c> testing entry-point before any
///     production behavioural change.
///
///     <para>
///         <b>Wave 2 Cat-C* migration.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>while + Task.Delay</c> loop driven by
///         <c>BrowserMode.RollupRecomputeIntervalSeconds</c> (default 300s).
///         Now subscribes to <see cref="TickCadence.Tick5m"/> -- the SAME
///         cadence as <see cref="FingerprintAbsorptionService"/> and
///         <see cref="FingerprintModeAbsorptionService"/> -- so all three sit
///         on the same wave window per
///         <c>project_absorption_services_migration</c>. A rollup recompute on
///         this tick sees consistent parent + per-mode state.
///     </para>
/// </summary>
public sealed class FingerprintRollupRecomputeService : IDisposable
{
    private readonly ILogger<FingerprintRollupRecomputeService> _logger;
    private readonly IFingerprintStore _store;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly IdentityOptions _options;
    private readonly bool _serviceEnabled;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public FingerprintRollupRecomputeService(
        ILogger<FingerprintRollupRecomputeService> logger,
        IFingerprintStore store,
        IFingerprintBrowserModeStore modeStore,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _store = store;
        _modeStore = modeStore;
        _options = options.Value.Identity;
        _serviceEnabled = _options.Enabled && _options.BrowserMode.Enabled;

        if (!_serviceEnabled)
        {
            _logger.LogDebug(
                "FingerprintRollupRecomputeService dormant: Identity.Enabled={Identity}, BrowserMode.Enabled={BrowserMode}",
                _options.Enabled, _options.BrowserMode.Enabled);
        }

        // Optional so existing direct-construction tests (FingerprintRollupRecomputeTests)
        // keep working without spinning up a real coordinator. The test rigs call
        // TickOnceAsync directly to drive a deterministic recompute.
        if (scheduleCoordinator is not null && _serviceEnabled)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick5m,
                "FingerprintRollupRecomputeService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: run one rollup pass.
    ///     Paired with <see cref="FingerprintAbsorptionService"/> and
    ///     <see cref="FingerprintModeAbsorptionService"/> on the same Tick5m
    ///     wave window so the rollup math sees consistent parent + per-mode
    ///     state.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_serviceEnabled) return;

        try
        {
            var (visited, written) = await TickOnceAsync(
                _options.BrowserMode.RollupMaxFingerprintsPerTick,
                dryRun: !_options.BrowserMode.RollupEnabled,
                ct);

            if (visited > 0)
                _logger.LogDebug(
                    "Rollup tick visited {Visited} fingerprints, wrote {Written} (dryRun={DryRun})",
                    visited, written, !_options.BrowserMode.RollupEnabled);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown; not an error.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rollup tick failed");
        }
    }

    /// <summary>
    ///     One rollup pass. Walks up to <paramref name="maxFingerprints"/>
    ///     fingerprints whose mode rows have evolved (oldest mode last_seen
    ///     first), computes the maturity-weighted mean of each fingerprint's
    ///     mode centroids, and writes it to the parent fingerprint row via
    ///     <see cref="IFingerprintStore.UpdateRollupCentroidAsync"/>. When
    ///     <paramref name="dryRun"/> is true the math runs but the parent row
    ///     is not written -- used by the BrowserMode.RollupEnabled=false gate
    ///     and the unit tests' inspection path.
    ///
    ///     Returns (visited fingerprints, fingerprints whose centroid was
    ///     written). Fingerprints with no mode rows are skipped silently;
    ///     fingerprints whose modes all sum to zero maturity are skipped (the
    ///     parent centroid stays whatever the parent absorption left it).
    /// </summary>
    public async Task<(int Visited, int Written)> TickOnceAsync(
        int maxFingerprints, bool dryRun, CancellationToken ct)
    {
        var fpIds = await _modeStore.ListFingerprintIdsWithModesAsync(maxFingerprints, ct);
        if (fpIds.Count == 0) return (0, 0);

        var visited = 0;
        var written = 0;
        foreach (var fpId in fpIds)
        {
            visited++;
            var modes = await _modeStore.GetModesAsync(fpId, ct);
            if (modes.Count == 0) continue;

            var rollup = ComputeRollup(modes);
            if (rollup is null) continue;

            if (!dryRun)
            {
                await _store.UpdateRollupCentroidAsync(
                    fpId, rollup.Value.Centroid, rollup.Value.Maturity, ct);
                written++;
            }
        }
        return (visited, written);
    }

    /// <summary>
    ///     Maturity-weighted mean of mode centroids. Each mode contributes
    ///     <c>centroid * centroid_maturity</c> to the sum; the final centroid
    ///     divides by the total maturity. Returns null when total maturity is
    ///     zero (every mode row is freshly seeded with no observations yet --
    ///     no reliable rollup signal).
    /// </summary>
    internal static (float[] Centroid, int Maturity)? ComputeRollup(IReadOnlyList<FingerprintBrowserMode> modes)
    {
        var dim = 0;
        for (var i = 0; i < modes.Count; i++)
            if (modes[i].Centroid.Length > dim) dim = modes[i].Centroid.Length;
        if (dim == 0) return null;

        var totalMaturity = 0;
        var weighted = new float[dim];
        for (var i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            if (m.CentroidMaturity <= 0) continue;
            var c = m.Centroid;
            var contribution = m.CentroidMaturity;
            for (var d = 0; d < c.Length && d < dim; d++)
                weighted[d] += c[d] * contribution;
            totalMaturity += contribution;
        }
        if (totalMaturity == 0) return null;

        for (var d = 0; d < dim; d++) weighted[d] /= totalMaturity;
        return (weighted, totalMaturity);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
