using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Thin hosted-service shim that calls
///     <see cref="IFingerprintPoolCollisionTracker.InitializeAsync"/> on the
///     pool-collision store at boot. Mirrors the pattern used by
///     <c>SessionPersistenceService</c> for <c>ISessionStore</c>: schema
///     creation lives on the store, lifecycle ownership on a HostedService.
/// </summary>
public sealed class PoolCollisionInitService : IHostedService
{
    private readonly IFingerprintPoolCollisionTracker _tracker;
    private readonly ILogger<PoolCollisionInitService> _logger;

    public PoolCollisionInitService(
        IFingerprintPoolCollisionTracker tracker,
        ILogger<PoolCollisionInitService> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tracker.InitializeAsync(cancellationToken);
            _logger.LogDebug("Pool collision store initialised");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pool collision store init failed; collisions will run hot-only until reboot");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
