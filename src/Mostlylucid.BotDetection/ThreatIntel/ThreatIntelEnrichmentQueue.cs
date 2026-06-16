using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

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
    private readonly Channel<ThreatSubject> _channel;
    private long _enqueued;
    private long _dropped;

    public ThreatIntelEnrichmentQueue(IOptions<BotDetectionOptions> options)
    {
        var capacity = Math.Max(1, options.Value.ThreatIntel.EnrichmentQueueCapacity);
        _channel = Channel.CreateBounded<ThreatSubject>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

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
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on
///         <see cref="ChannelReader{T}.ReadAllAsync"/>. Now subscribes to
///         <see cref="TickCadence.Tick10s"/> via
///         <see cref="IScheduleCoordinator"/>; each tick drains whatever is
///         available right now via <see cref="ChannelReader{T}.TryRead"/> and
///         routes each subject through <see cref="IThreatIntelCoordinator.EnrichAsync"/>
///         under the same per-subject timeout. The coordinator-disabled and
///         no-live-providers checks happen inside the tick handler -- a dormant
///         coordinator simply early-returns each tick.
///     </para>
///     <para>
///         Latency budget: enrichment subjects can sit in the queue up to one
///         tick interval (~10s) before processing starts. The queue is
///         <see cref="BoundedChannelFullMode.DropOldest"/> so a tick that skips
///         while enrichment work is in flight is harmless -- the oldest item is
///         the most stale to begin with. Cadence matches
///         <c>BackgroundEnrichmentService</c> for operational symmetry.
///     </para>
/// </summary>
internal sealed class ThreatIntelEnrichmentService : IDisposable
{
    private readonly ThreatIntelEnrichmentQueue _queue;
    private readonly IThreatIntelCoordinator _coordinator;
    private readonly ILogger<ThreatIntelEnrichmentService> _logger;
    private readonly TimeSpan _perSubjectTimeout;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public ThreatIntelEnrichmentService(
        ThreatIntelEnrichmentQueue queue,
        IThreatIntelCoordinator coordinator,
        IOptions<BotDetectionOptions> options,
        ILogger<ThreatIntelEnrichmentService> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _queue = queue;
        _coordinator = coordinator;
        _perSubjectTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.ThreatIntel.EnrichmentTimeoutSeconds));
        _logger = logger;

        // Optional so existing direct-construction tests that exercise the
        // queue + coordinator in isolation keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "ThreatIntelEnrichmentService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: short-circuit when the
    ///     coordinator is disabled or has no live providers, otherwise drain
    ///     whatever subjects landed since the last tick via
    ///     <see cref="ChannelReader{T}.TryRead"/> and route each through
    ///     <see cref="IThreatIntelCoordinator.EnrichAsync"/> under the
    ///     per-subject timeout. Single-reader by contract (the channel is
    ///     configured with <c>SingleReader = true</c>) so no fan-out is
    ///     attempted -- enrichments are processed sequentially within the tick.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_coordinator.IsEnabled) return;
        if (!_coordinator.Providers.Any(p => p.Mode == ThreatIntelMode.Live)) return;

        while (_queue.Reader.TryRead(out var subject))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_perSubjectTimeout);
                await _coordinator.EnrichAsync(subject, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "ThreatIntel enrichment for {Subject} timed out ({Timeout} cap)",
                    subject, _perSubjectTimeout);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ThreatIntel enrichment failed for {Subject}", subject);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
