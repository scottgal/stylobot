using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Live-feed relay for remote-mode dashboard hosts. Opens a long-lived SignalR client
///     connection to the gateway's invalidation hub (configured via
///     <see cref="DashboardSourceOptions.Live"/>); on every <c>BroadcastInvalidation</c>
///     event from the gateway, re-broadcasts the beacon into the local hub so the
///     browser HTMX coordinator refreshes the affected partials.
///
///     <para>
///     The hub speaks beacons only - no data payloads cross the wire. The browser
///     reacts by re-fetching the affected REST endpoints (which go to the gateway
///     via the Remote* stores). End-to-end: gateway detection -> gateway hub beacon
///     -> this relay -> local hub -> browser HTMX -> Remote* -> gateway REST -> render.
///     </para>
///
///     <para>
///     Auto-reconnect is on. On startup the relay logs the configured URL and any
///     connection failures - operators can grep for "BeaconRelay" to confirm the
///     pipe is up. Self-disables when not in remote mode (checks Live.Type on startup).
///     </para>
/// </summary>
internal sealed class SignalRBeaconRelay : BackgroundService
{
    private readonly DashboardSourceOptions _source;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _localHub;
    private readonly ILogger<SignalRBeaconRelay> _logger;

    private HubConnection? _connection;

    public SignalRBeaconRelay(
        DashboardSourceOptions source,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> localHub,
        ILogger<SignalRBeaconRelay> logger)
    {
        _source = source;
        _localHub = localHub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_source.Live.Type != DashboardLiveFeedType.SignalR)
        {
            _logger.LogInformation("BeaconRelay: live feed disabled (StyloBot:Source:Live:Type != SignalR); no relay started");
            return;
        }
        if (string.IsNullOrWhiteSpace(_source.Live.Url))
        {
            _logger.LogWarning("BeaconRelay: StyloBot:Source:Live:Url is empty; cannot start relay");
            return;
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(_source.Live.Url, opts =>
            {
                if (!string.IsNullOrEmpty(_source.Pull.ApiKey))
                    opts.Headers["X-SB-Api-Key"] = _source.Pull.ApiKey;
            })
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string>(nameof(IStyloBotDashboardHub.BroadcastInvalidation), async signal =>
        {
            try
            {
                await _localHub.Clients.All.BroadcastInvalidation(signal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BeaconRelay: failed to forward invalidation '{Signal}'", signal);
            }
        });

        _connection.On<string, string>(nameof(IStyloBotDashboardHub.BroadcastAttackArc), async (countryCode, riskBand) =>
        {
            try
            {
                await _localHub.Clients.All.BroadcastAttackArc(countryCode, riskBand);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BeaconRelay: failed to forward attack arc {Country}/{Risk}", countryCode, riskBand);
            }
        });

        _connection.Closed += ex =>
        {
            if (ex is not null) _logger.LogWarning(ex, "BeaconRelay: gateway connection closed");
            return Task.CompletedTask;
        };
        _connection.Reconnecting += ex =>
        {
            _logger.LogInformation("BeaconRelay: reconnecting to gateway hub at {Url}", _source.Live.Url);
            return Task.CompletedTask;
        };
        _connection.Reconnected += id =>
        {
            _logger.LogInformation("BeaconRelay: reconnected to gateway hub ({ConnectionId})", id);
            return Task.CompletedTask;
        };

        // Retry the initial connect indefinitely. The gateway may not be reachable yet
        // when the relay starts - HubConnection.StartAsync throws synchronously on first
        // failure, so we wrap it in a loop with backoff.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _connection.StartAsync(stoppingToken);
                _logger.LogInformation("BeaconRelay: connected to gateway hub at {Url}", _source.Live.Url);
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "BeaconRelay: gateway hub unreachable at {Url}; retrying in 10s", _source.Live.Url);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            try { await _connection.DisposeAsync(); } catch { /* shutting down; swallow */ }
        }
        await base.StopAsync(cancellationToken);
    }
}
