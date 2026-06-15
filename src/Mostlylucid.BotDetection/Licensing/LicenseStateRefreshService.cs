using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Refreshes ILicenseState every 60 seconds from BotDetectionOptions.Licensing.Token.
///     Handles grace period start (first expiry transition) and grace period clear (renewal).
///     Also initializes the SQLite license_state table on first tick.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>PeriodicTimer(60s)</c> loop; now subscribes to
///         <see cref="TickCadence.Tick1m"/> on <see cref="IScheduleCoordinator"/>.
///         The first tick performs the one-shot
///         <see cref="ILicenseGraceStore.InitializeAsync"/> call that used to
///         run before the loop -- after that the handler is pure refresh
///         logic. See <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
internal sealed class LicenseStateRefreshService : IDisposable
{
    private readonly LicenseState _licenseState;
    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly ILicenseGraceStore _graceStore;
    private readonly ILogger<LicenseStateRefreshService> _logger;
    private readonly IDisposable _subscription;
    private int _disposed;
    private int _initialized;

    public LicenseStateRefreshService(
        LicenseState licenseState,
        IOptionsMonitor<BotDetectionOptions> options,
        ILicenseGraceStore graceStore,
        ILogger<LicenseStateRefreshService> logger,
        IScheduleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _licenseState = licenseState;
        _options = options;
        _graceStore = graceStore;
        _logger = logger;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1m,
            "LicenseStateRefreshService",
            CostHint.Low,
            OnTickAsync);
    }

    /// <summary>
    ///     The coordinator's tick handler. First tick: initializes the SQLite
    ///     license_state store, then refreshes. Subsequent ticks: refresh only.
    ///     Public so tests can drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            try
            {
                await _graceStore.InitializeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to initialize license grace store; using in-memory FOSS license state until the next refresh.");
            }
        }

        await RefreshAsync(ct);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var token = _options.CurrentValue.Licensing?.Token;
            var claims = LicenseTokenParser.TryParse(token);

            if (claims == null)
            {
                _licenseState.Update(LicenseStateSnapshot.Foss);
                return;
            }

            var graceStartedAt = await _graceStore.GetGraceStartedAtAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var isActive = claims.ExpiresAt == null || now < claims.ExpiresAt;

            if (!isActive && claims.GraceEligible && graceStartedAt == null)
            {
                graceStartedAt = now;
                await _graceStore.SetGraceStartedAtAsync(now, ct);
                _logger.LogWarning(
                    "StyloBot license expired. 30-day grace period started - detection active, learning paused. " +
                    "Renew at https://stylobot.net");
            }

            if (isActive && graceStartedAt != null)
            {
                await _graceStore.ClearGraceStartedAtAsync(ct);
                graceStartedAt = null;
                _logger.LogInformation("StyloBot license renewed. Learning resumed.");
            }

            var snapshot = LicenseStateSnapshot.Compute(claims.ExpiresAt, claims.GraceEligible, graceStartedAt);
            _licenseState.Update(snapshot);

            if (snapshot.LogOnly)
                _logger.LogWarning(
                    "StyloBot license grace period expired. Running in log-only mode. " +
                    "Renew at https://stylobot.net");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing license state");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}