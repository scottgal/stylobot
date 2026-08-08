using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.Ephemeral;

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
///         must not lie about coverage. This is a legitimate use of a plain
///         <see cref="IHostedService"/> for one-shot startup-blocking work (the
///         standing no-BackgroundService rule's own exception for schema-init-before-
///         traffic-style warmup), not the violation being fixed here.
///       </item>
///       <item>
///         Steady state: each provider subscribes independently to
///         <see cref="IScheduleCoordinator"/>'s <see cref="TickCadence.Tick5m"/>, gated
///         on "elapsed since last attempt &gt;= provider.RefreshInterval" - the same
///         idiom every other Wave-2-migrated fetcher in this codebase uses
///         (<c>WellKnownBotRefreshService</c>, <c>GeoLite2UpdateService</c>,
///         <c>GoodBotIpRangeRefreshService</c>). Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         per-provider <c>Task.Delay</c> loop and a hand-rolled exponential-backoff
///         retry; the tick model replaces both — a failed attempt is retried at the
///         next natural Tick5m rather than a growing custom delay, and the
///         coordinator's own fault isolation (one provider's exception doesn't stop
///         the others) replaces the per-loop try/catch restart machinery. See
///         <c>feedback_no_background_services</c>.
///       </item>
///     </list>
/// </summary>
internal sealed class ThreatIntelRefreshService : IHostedService, IDisposable
{
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ThreatIntelOptions _options;
    private readonly ILogger<ThreatIntelRefreshService> _logger;
    private readonly IScheduleCoordinator _scheduleCoordinator;
    private readonly TypedSignalSink<ThreatIntelRefreshedSignal>? _refreshSignals;

    // Tracks per-provider "last refresh failed" so RaiseRefreshed can set
    // RecoveredFromFailure=true when the next successful refresh lands.
    private readonly ConcurrentDictionary<string, bool> _lastFailed = new();

    // Tracks per-provider "when did we last attempt a refresh" (success or failure -
    // an attempt, not just a success, is what the cadence gate measures, matching the
    // original Task.Delay loop's semantics of "wait RefreshInterval after each try").
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAttemptUtc = new();

    private readonly List<IDisposable> _subscriptions = new();

    public ThreatIntelRefreshService(
        IThreatIntelCoordinator coordinator,
        IOptions<BotDetectionOptions> options,
        ILogger<ThreatIntelRefreshService> logger,
        IScheduleCoordinator scheduleCoordinator,
        TypedSignalSink<ThreatIntelRefreshedSignal>? refreshSignals = null)
    {
        _coordinator = coordinator;
        _options = options.Value.ThreatIntel;
        _logger = logger;
        _scheduleCoordinator = scheduleCoordinator;
        _refreshSignals = refreshSignals;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_coordinator.IsEnabled)
        {
            _logger.LogInformation("Threat intel disabled; refresh service inactive");
            return;
        }

        var offline = _coordinator.Providers.Where(p => p.Mode == ThreatIntelMode.Offline).ToArray();
        if (offline.Length == 0)
        {
            _logger.LogInformation("Threat intel enabled but no offline providers registered");
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
            // Kick off first refreshes opportunistically. The Tick5m subscriptions below
            // also cover them on schedule, but a 0-delay first attempt lets the cache
            // populate ASAP.
            foreach (var provider in offline)
                _ = BootstrapAsync(provider, TimeSpan.FromSeconds(_options.StartupFetchTimeoutSeconds), cancellationToken);
        }

        // Steady state: one ScheduleCoordinator subscription per provider. Bootstrap's
        // attempt (above) already counts toward the cadence gate via _lastAttemptUtc, so
        // the first steady-state check naturally waits a full RefreshInterval past
        // whichever moment bootstrap completed for that provider - no separate stagger
        // bookkeeping needed; per-provider network jitter during the parallel bootstrap
        // already spreads them out.
        foreach (var provider in offline)
        {
            _subscriptions.Add(_scheduleCoordinator.Subscribe(
                TickCadence.Tick5m,
                $"{nameof(ThreatIntelRefreshService)}:{provider.Name}",
                CostHint.Medium,
                (now, ct) => OnTickAsync(provider, now, ct)));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task OnTickAsync(IThreatIntelProvider provider, DateTimeOffset now, CancellationToken ct)
    {
        if (!_coordinator.IsEnabled) return;

        if (_lastAttemptUtc.TryGetValue(provider.Name, out var last) && now - last < provider.RefreshInterval)
            return; // Not yet due.

        _lastAttemptUtc[provider.Name] = now;

        try
        {
            await provider.RefreshAsync(subject: null, ct);
            RaiseRefreshed(provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown.
        }
        catch (Exception ex)
        {
            MarkFailed(provider);
            // Provider's RefreshAsync has its own catch-and-log so most failures don't
            // reach this handler; an exception making it here is something the inner
            // try/catch missed. No custom backoff - the next Tick5m naturally retries,
            // and ScheduleCoordinator's fault isolation keeps every other provider's
            // subscription running regardless.
            _logger.LogError(ex, "Threat-intel refresh for {Provider} failed; retrying at the next eligible tick",
                provider.Name);
        }
    }

    private async Task BootstrapAsync(IThreatIntelProvider provider, TimeSpan timeout, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        try
        {
            await provider.RefreshAsync(subject: null, cts.Token);
            RaiseRefreshed(provider);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !outer.IsCancellationRequested)
        {
            MarkFailed(provider);
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
            MarkFailed(provider);
            if (_options.BlockStartupOnFirstFetch)
            {
                _logger.LogCritical(ex, "Threat-intel provider {Provider} bootstrap failed", provider.Name);
                throw;
            }
            _logger.LogWarning(ex,
                "Threat-intel provider {Provider} bootstrap failed; non-blocking mode - continuing without this provider's intel",
                provider.Name);
        }
        finally
        {
            _lastAttemptUtc[provider.Name] = DateTimeOffset.UtcNow;
        }
    }

    private void RaiseRefreshed(IThreatIntelProvider provider)
    {
        var wasFailed = _lastFailed.TryRemove(provider.Name, out var f) && f;
        _refreshSignals?.Raise(
            signal: ThreatIntelRefreshedSignal.Key.Name,
            payload: new ThreatIntelRefreshedSignal
            {
                Provider = provider.Name,
                Timestamp = DateTimeOffset.UtcNow,
                RecoveredFromFailure = wasFailed,
            });
    }

    private void MarkFailed(IThreatIntelProvider provider)
        => _lastFailed[provider.Name] = true;

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* coordinator already torn down */ }
        }
        _subscriptions.Clear();
    }
}
