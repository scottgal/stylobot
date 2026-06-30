using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Cache of signature response coordinators (LRU).
///     All coordinators share a single SignalSink owned by this cache.
///     This eliminates the O(N) memory growth from per-coordinator sinks.
/// </summary>
public sealed class SignatureResponseCoordinatorCache : IAsyncDisposable
{
    private readonly SlidingCacheAtom<string, SignatureResponseCoordinator> _cache;
    private readonly ILogger<SignatureResponseCoordinatorCache> _logger;
    private readonly SignalSink _sharedSink;
    private readonly UpstreamHealthGate? _upstreamHealth;

    public SignatureResponseCoordinatorCache(
        ILogger<SignatureResponseCoordinatorCache> logger,
        int maxSignatures = 5000,
        TimeSpan? ttl = null,
        SignalSink? sharedSink = null,
        TimeSpan? cleanupInterval = null,
        UpstreamHealthGate? upstreamHealth = null)
    {
        _logger = logger;
        _upstreamHealth = upstreamHealth;

        _sharedSink = sharedSink ?? new SignalSink(
            Math.Min(maxSignatures * 20, 50_000),
            TimeSpan.FromHours(1));

        _cache = new SlidingCacheAtom<string, SignatureResponseCoordinator>(
            async (signature, ct) =>
            {
                _logger.LogDebug("Creating SignatureResponseCoordinator for {Signature}", signature);

                return new SignatureResponseCoordinator(signature, logger, _sharedSink, _upstreamHealth);
            },
            ttl ?? TimeSpan.FromMinutes(30),
            (ttl ?? TimeSpan.FromMinutes(30)) * 2,
            maxSignatures,
            Environment.ProcessorCount,
            10,
            null, // No external signals
            cleanupInterval: cleanupInterval ?? TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        _logger.LogInformation("SignatureResponseCoordinatorCache disposed");
    }

    public async Task<SignatureResponseCoordinator> GetOrCreateAsync(
        string signature,
        CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrComputeAsync(signature, cancellationToken);
    }
}