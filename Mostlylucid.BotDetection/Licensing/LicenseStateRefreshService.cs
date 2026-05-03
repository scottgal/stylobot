using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Refreshes ILicenseState every 60 seconds from BotDetectionOptions.Licensing.Token.
///     Handles grace period start (first expiry transition) and grace period clear (renewal).
///     Also initializes the SQLite license_state table on startup.
/// </summary>
internal sealed class LicenseStateRefreshService : BackgroundService
{
    private readonly LicenseState _licenseState;
    private readonly IOptionsMonitor<BotDetectionOptions> _options;
    private readonly SqliteLicenseGraceStore _graceStore;
    private readonly ILogger<LicenseStateRefreshService> _logger;

    public LicenseStateRefreshService(
        LicenseState licenseState,
        IOptionsMonitor<BotDetectionOptions> options,
        SqliteLicenseGraceStore graceStore,
        ILogger<LicenseStateRefreshService> logger)
    {
        _licenseState = licenseState;
        _options = options;
        _graceStore = graceStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _graceStore.InitializeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize license grace store; using in-memory FOSS license state until the next refresh.");
        }

        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown.
        }
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
}
