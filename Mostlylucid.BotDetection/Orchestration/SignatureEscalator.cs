using Microsoft.Extensions.Logging;
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
    private readonly bool _ownsSink;

    public SignatureResponseCoordinatorCache(
        ILogger<SignatureResponseCoordinatorCache> logger,
        int maxSignatures = 5000,
        TimeSpan? ttl = null,
        SignalSink? sharedSink = null)
    {
        _logger = logger;

        if (sharedSink is not null)
        {
            _sharedSink = sharedSink;
            _ownsSink = false;
        }
        else
        {
            _sharedSink = new SignalSink(
                Math.Min(maxSignatures * 20, 50_000),
                TimeSpan.FromHours(1));
            _ownsSink = true;
        }

        _cache = new SlidingCacheAtom<string, SignatureResponseCoordinator>(
            async (signature, ct) =>
            {
                _logger.LogDebug("Creating SignatureResponseCoordinator for {Signature}", signature);

                return new SignatureResponseCoordinator(signature, logger, _sharedSink);
            },
            ttl ?? TimeSpan.FromMinutes(30),
            (ttl ?? TimeSpan.FromMinutes(30)) * 2,
            maxSignatures,
            Environment.ProcessorCount,
            10,
            null); // No external signals
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        // SignalSink does not implement IDisposable; it is released to GC.
        // _ownsSink tracks whether we created it (for future IDisposable support).
        _logger.LogInformation("SignatureResponseCoordinatorCache disposed");
    }

    public async Task<SignatureResponseCoordinator> GetOrCreateAsync(
        string signature,
        CancellationToken cancellationToken = default)
    {
        return await _cache.GetOrComputeAsync(signature, cancellationToken);
    }
}