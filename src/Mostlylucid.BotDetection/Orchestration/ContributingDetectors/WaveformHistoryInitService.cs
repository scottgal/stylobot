using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     HostedService that creates the SQLite schema for
///     <see cref="WaveformHistoryStore"/> at boot. Mirrors
///     <c>SessionPersistenceService</c> / <c>PoolCollisionInitService</c>:
///     schema lives on the store; lifecycle on the host.
/// </summary>
public sealed class WaveformHistoryInitService : IHostedService
{
    private readonly WaveformHistoryStore _store;
    private readonly ILogger<WaveformHistoryInitService> _logger;

    public WaveformHistoryInitService(
        WaveformHistoryStore store,
        ILogger<WaveformHistoryInitService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken);
            _logger.LogDebug("Waveform history store initialised");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Waveform history store init failed; analyses will run hot-only until reboot");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
