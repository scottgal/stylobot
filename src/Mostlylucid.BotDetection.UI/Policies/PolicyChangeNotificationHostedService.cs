using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     Listens to <see cref="IPolicyRuleStore.Changed"/> and republishes the
///     event through <see cref="IPolicyChangeBroadcaster"/>. One arrow in one
///     direction: store mutates -> hosted service notices -> broadcaster
///     fans out -> SignalR (or commercial transport) wakes the browsers.
///
///     <para>
///     The hosted service is the only adapter between the rule-store world
///     (which knows nothing about SignalR) and the UI world (which doesn't
///     own any persistence). Keep it small; the cost on the hot path is one
///     event-handler invocation and one fire-and-forget Task per mutation.
///     </para>
/// </summary>
public sealed class PolicyChangeNotificationHostedService : IHostedService
{
    private readonly IPolicyRuleStore _store;
    private readonly IPolicyChangeBroadcaster _broadcaster;
    private readonly ILogger<PolicyChangeNotificationHostedService> _logger;
    private EventHandler<PolicyRuleStoreChangedEventArgs>? _handler;

    public PolicyChangeNotificationHostedService(
        IPolicyRuleStore store,
        IPolicyChangeBroadcaster broadcaster,
        ILogger<PolicyChangeNotificationHostedService> logger)
    {
        _store = store;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        // Captured handler so StopAsync can detach the exact same delegate
        // instance -- otherwise a re-subscribe on host restart would leak.
        _handler = (_, e) =>
        {
            // Detach the broadcast from the store's reload thread. The store's
            // event invocation is synchronous and a slow broadcaster (a Redis
            // round-trip, for example) must not back-pressure the reload
            // pipeline. Catch + log on the background task so a transient
            // broadcaster failure can't crash the host.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _broadcaster.PublishAsync(e.ChangedScope, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Policy change broadcast failed for scope {Scope}", e.ChangedScope);
                }
            });
        };

        _store.Changed += _handler;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        if (_handler is not null)
        {
            _store.Changed -= _handler;
            _handler = null;
        }
        return Task.CompletedTask;
    }
}
