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

    public PolicyEffectivenessCacheHostedService(IPolicyEffectivenessCache cache)
    {
        _cache = cache;
    }

    public Task StartAsync(CancellationToken cancellationToken) => _cache.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => _cache.StopAsync(cancellationToken);
}
