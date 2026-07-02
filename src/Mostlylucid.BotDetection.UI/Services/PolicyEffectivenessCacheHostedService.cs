using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Policies.Telemetry;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Hosted-service shim that owns the lifecycle of the singleton
///     <see cref="IPolicyEffectivenessCache"/>. The cache itself is registered
///     as a singleton via its interface so other read paths see the same
///     instance the evaluator writes to; this shim only forwards
///     <see cref="StartAsync"/> / <see cref="StopAsync"/> to the cache so its
///     background drainer starts and shuts cleanly with the host.
/// </summary>
public sealed class PolicyEffectivenessCacheHostedService : IHostedService
{
    private readonly IPolicyEffectivenessCache _cache;
    private readonly bool _isRemoteMode;

    public PolicyEffectivenessCacheHostedService(
        IPolicyEffectivenessCache cache,
        Adapters.Remote.DashboardSourceOptions? sourceOptions = null)
    {
        _cache = cache;
        // On a remote-mode host the policy evaluator (which feeds this cache) runs on
        // the gateway, not here — the cache stays an empty DI shim and the drainer has
        // nothing to drain. Skip it so the upstream host runs no state producer
        // (feedback_upstream_owns_no_stylobot_state); the singleton stays resolvable for
        // the presenter, which reads gateway data over REST.
        _isRemoteMode = sourceOptions?.Pull?.Type == Adapters.Remote.DashboardSourceType.Rest;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => _isRemoteMode ? Task.CompletedTask : _cache.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => _isRemoteMode ? Task.CompletedTask : _cache.StopAsync(cancellationToken);
}
