using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Bounded channel that detaches the hot-path contributor from live-provider
///     I/O. Contributors call <see cref="TryEnqueue"/> after a cache miss; a
///     background worker drains and calls <see cref="IThreatIntelCoordinator.EnrichAsync"/>
///     which fans out to live providers. The provider base handles quota /
///     breaker / coalescing internally.
///
///     <para>Capacity 500, DropOldest on full - matches the existing
///     BackgroundEnrichmentService pattern so we share operational understanding.
///     Same-subject coalescing happens INSIDE the live provider base, so duplicate
///     enqueues for the same IP are cheap (one HTTP fetch regardless).</para>
/// </summary>
public sealed class ThreatIntelEnrichmentQueue
{
    private readonly Channel<ThreatSubject> _channel = Channel.CreateBounded<ThreatSubject>(
        new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private long _enqueued;
    private long _dropped;

    /// <summary>Approximate depth at the moment of the call.</summary>
    public int Depth => _channel.Reader.Count;

    /// <summary>Total successful enqueues since process start.</summary>
    public long TotalEnqueued => Interlocked.Read(ref _enqueued);

    /// <summary>Total dropped enqueues (channel was full at write time).</summary>
    public long TotalDropped => Interlocked.Read(ref _dropped);

    /// <summary>
    ///     Try to enqueue a subject for background enrichment. Non-blocking; returns
    ///     false when the channel is full (oldest already dropped under DropOldest).
    /// </summary>
    public bool TryEnqueue(ThreatSubject subject)
    {
        if (_channel.Writer.TryWrite(subject))
        {
            Interlocked.Increment(ref _enqueued);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    internal ChannelReader<ThreatSubject> Reader => _channel.Reader;
}

/// <summary>
///     Drains <see cref="ThreatIntelEnrichmentQueue"/> and routes each subject
///     through <see cref="IThreatIntelCoordinator.EnrichAsync"/>. One reader, no
///     concurrency control needed - per-provider in-flight coalescing happens
///     inside <see cref="ThreatIntelLiveProviderBase"/>.
/// </summary>
internal sealed class ThreatIntelEnrichmentService : BackgroundService
{
    private readonly ThreatIntelEnrichmentQueue _queue;
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ILogger<ThreatIntelEnrichmentService> _logger;

    public ThreatIntelEnrichmentService(
        ThreatIntelEnrichmentQueue queue,
        IThreatIntelCoordinator coordinator,
        ILogger<ThreatIntelEnrichmentService> logger)
    {
        _queue = queue;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_coordinator.IsEnabled)
        {
            _logger.LogInformation("Threat intel disabled; enrichment service inactive");
            return;
        }

        var hasLive = _coordinator.Providers.Any(p => p.Mode == ThreatIntelMode.Live);
        if (!hasLive)
        {
            _logger.LogInformation("Threat intel enabled but no live providers registered; enrichment service inactive");
            return;
        }

        _logger.LogInformation("ThreatIntelEnrichmentService started (queue capacity=500, single-reader)");

        try
        {
            await foreach (var subject in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));   // per-subject hard cap
                    await _coordinator.EnrichAsync(subject, cts.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug("ThreatIntel enrichment for {Subject} timed out (10s cap)", subject);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ThreatIntel enrichment failed for {Subject}", subject);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
