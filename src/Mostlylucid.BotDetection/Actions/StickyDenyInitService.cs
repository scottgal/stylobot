using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Hosted-service shim that calls
///     <see cref="SqliteStickyDenyStore.InitializeAsync"/> at boot. Same
///     pattern as <c>PoolCollisionInitService</c>: schema creation lives
///     on the store, lifecycle ownership on a HostedService.
/// </summary>
public sealed class StickyDenyInitService : IHostedService
{
    private readonly SqliteStickyDenyStore _store;
    private readonly ILogger<StickyDenyInitService> _logger;

    public StickyDenyInitService(
        SqliteStickyDenyStore store,
        ILogger<StickyDenyInitService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken);
            _logger.LogDebug("Sticky-deny store initialised");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Sticky-deny store init failed; sticky-deny will run hot-only and lose state on restart");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
