using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Drives <see cref="IThreatIntelProvider.RefreshAsync"/> for every registered
///     offline provider. Two phases:
///     <list type="number">
///       <item>
///         Bootstrap: when <see cref="ThreatIntelOptions.BlockStartupOnFirstFetch"/>
///         is true (FOSS default), <see cref="StartAsync"/> awaits the first refresh
///         of each enabled provider in parallel, capped at
///         <see cref="ThreatIntelOptions.StartupFetchTimeoutSeconds"/> per provider.
///         If any provider fails, log fatal + throw - the operator opted in and we
///         must not lie about coverage.
///       </item>
///       <item>
///         Steady state: each provider runs on its own staggered timer. First
///         post-bootstrap tick fires at <c>now + Random(0..StaggerWindowSeconds)</c>
///         then ticks at the provider's <c>RefreshInterval</c>. Avoids spike of N
///         concurrent fetches on the same wall-clock tick.
///       </item>
///     </list>
/// </summary>
internal sealed class ThreatIntelRefreshService : BackgroundService
{
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ThreatIntelOptions _options;
    private readonly ILogger<ThreatIntelRefreshService> _logger;

    public ThreatIntelRefreshService(
        IThreatIntelCoordinator coordinator,
        IOptions<BotDetectionOptions> options,
        ILogger<ThreatIntelRefreshService> logger)
    {
        _coordinator = coordinator;
        _options = options.Value.ThreatIntel;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_coordinator.IsEnabled)
        {
            _logger.LogInformation("Threat intel disabled; refresh service inactive");
            await base.StartAsync(cancellationToken);
            return;
        }

        var offline = _coordinator.Providers.Where(p => p.Mode == ThreatIntelMode.Offline).ToArray();
        if (offline.Length == 0)
        {
            _logger.LogInformation("Threat intel enabled but no offline providers registered");
            await base.StartAsync(cancellationToken);
            return;
        }

        if (_options.BlockStartupOnFirstFetch)
        {
            _logger.LogInformation(
                "Threat intel bootstrap: blocking startup on first fetch of {Count} provider(s), per-provider timeout {Timeout}s",
                offline.Length, _options.StartupFetchTimeoutSeconds);

            var perTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.StartupFetchTimeoutSeconds));
            var tasks = offline.Select(p => BootstrapAsync(p, perTimeout, cancellationToken)).ToArray();
            await Task.WhenAll(tasks);
        }
        else
        {
            _logger.LogInformation(
                "Threat intel: non-blocking bootstrap; first refreshes will run in the background");
            // Kick off first refreshes opportunistically. ExecuteAsync also runs them on
            // their schedules, but a 0-delay first tick lets the cache populate ASAP.
            foreach (var provider in offline)
                _ = BootstrapAsync(provider, TimeSpan.FromSeconds(_options.StartupFetchTimeoutSeconds), cancellationToken);
        }

        await base.StartAsync(cancellationToken);
    }

    private async Task BootstrapAsync(IThreatIntelProvider provider, TimeSpan timeout, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        try
        {
            await provider.RefreshAsync(subject: null, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !outer.IsCancellationRequested)
        {
            var msg = $"Threat-intel provider {provider.Name} bootstrap timed out after {timeout.TotalSeconds:F0}s";
            if (_options.BlockStartupOnFirstFetch)
            {
                _logger.LogCritical(msg);
                throw new TimeoutException(msg);
            }
            _logger.LogWarning(msg + "; non-blocking mode - continuing without this provider's intel");
        }
        catch (Exception ex)
        {
            if (_options.BlockStartupOnFirstFetch)
            {
                _logger.LogCritical(ex, "Threat-intel provider {Provider} bootstrap failed", provider.Name);
                throw;
            }
            _logger.LogWarning(ex,
                "Threat-intel provider {Provider} bootstrap failed; non-blocking mode - continuing without this provider's intel",
                provider.Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_coordinator.IsEnabled) return;

        var offline = _coordinator.Providers.Where(p => p.Mode == ThreatIntelMode.Offline).ToArray();
        if (offline.Length == 0) return;

        // Each provider runs on its own loop with a staggered first delay. Tasks
        // share the same stoppingToken so a host-stop cancels every loop together.
        var window = Math.Max(0, _options.StaggerWindowSeconds);
        var random = new Random();
        var loops = offline.Select(p =>
        {
            var offset = window == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(random.Next(0, window));
            return RunLoopAsync(p, offset, stoppingToken);
        }).ToArray();

        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(IThreatIntelProvider provider, TimeSpan initialDelay, CancellationToken ct)
    {
        try
        {
            if (initialDelay > TimeSpan.Zero) await Task.Delay(initialDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await provider.RefreshAsync(subject: null, ct);
                await Task.Delay(provider.RefreshInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Threat-intel refresh loop for {Provider} crashed; loop will not restart until host restart",
                provider.Name);
        }
    }
}
