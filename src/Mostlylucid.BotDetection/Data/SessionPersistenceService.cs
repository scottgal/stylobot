using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Persists completed sessions to <see cref="IDetectionArchive"/>. Subscribes
///     to <see cref="SessionStore.SessionFinalized"/> and writes asynchronously
///     via a bounded channel to keep the request pipeline off the SQLite
///     write path.
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> over the
///         bounded channel. Now subscribes to <see cref="TickCadence.Tick10s"/>
///         via <see cref="IScheduleCoordinator"/>; each tick drains whatever
///         landed since the last tick via
///         <see cref="ChannelReader{T}.TryRead"/> and persists each snapshot
///         sequentially. The 10s cadence keeps session writes off the request
///         path while bounding the worst-case persistence latency.
///     </para>
///     <para>
///         Store-init at boot and graceful shutdown drain (flush active
///         sessions + persist any remaining channel entries) moved out to
///         <see cref="SessionPersistenceLifecycleHostedService"/> so this
///         class itself is loop-free and matches the Cat-B recipe.
///     </para>
/// </summary>
public sealed class SessionPersistenceService : IDisposable
{
    private readonly IDetectionArchive _store;
    private readonly SessionStore _sessionStore;
    private readonly ILogger<SessionPersistenceService> _logger;
    private readonly Channel<(SessionSnapshot Snapshot, IReadOnlyList<SessionRequest> Requests)> _channel;
    private readonly Action<SessionSnapshot, IReadOnlyList<SessionRequest>> _onFinalizedHandler;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public SessionPersistenceService(
        IDetectionArchive store,
        SessionStore sessionStore,
        ILogger<SessionPersistenceService> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _store = store;
        _sessionStore = sessionStore;
        _logger = logger;
        _channel = Channel.CreateBounded<(SessionSnapshot, IReadOnlyList<SessionRequest>)>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

        // Wire the event source. SessionFinalized is event-driven (sessions
        // fire whenever they expire / complete); the channel decouples the
        // event firing thread from the SQLite write thread.
        _onFinalizedHandler = OnSessionFinalized;
        _sessionStore.SessionFinalized += _onFinalizedHandler;

        // Optional so existing direct-construction tests that exercise
        // OnSessionFinalized / PersistSessionAsync in isolation keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "SessionPersistenceService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>Channel reader exposed for the lifecycle host's shutdown drain.</summary>
    internal ChannelReader<(SessionSnapshot Snapshot, IReadOnlyList<SessionRequest> Requests)> Reader => _channel.Reader;

    /// <summary>Internal hook so the lifecycle host can complete the writer on shutdown.</summary>
    internal void CompleteWriter() => _channel.Writer.TryComplete();

    /// <summary>Visible depth for tests + dashboards.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain whatever
    ///     finalized sessions landed since the last tick via
    ///     <see cref="ChannelReader{T}.TryRead"/> and persist each
    ///     sequentially. SQLite writes are the bottleneck; processing within a
    ///     single tick keeps the writer contention bounded.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        while (_channel.Reader.TryRead(out var item))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await PersistSessionAsync(item.Snapshot, item.Requests, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist session for {Signature}", item.Snapshot.Signature);
            }
        }
    }

    /// <summary>
    ///     Internal entry point used by
    ///     <see cref="SessionPersistenceLifecycleHostedService"/> during
    ///     graceful shutdown to flush any sessions that landed on the channel
    ///     after the last tick fired.
    /// </summary>
    internal async Task DrainAndPersistAllAsync(CancellationToken ct)
    {
        await foreach (var (snapshot, requests) in _channel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await PersistSessionAsync(snapshot, requests, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist session during shutdown for {Signature}", snapshot.Signature);
            }
        }
    }

    private void OnSessionFinalized(SessionSnapshot snapshot, IReadOnlyList<SessionRequest> requests)
    {
        _channel.Writer.TryWrite((snapshot, requests));
    }

    private async Task PersistSessionAsync(
        SessionSnapshot snapshot,
        IReadOnlyList<SessionRequest> requests,
        CancellationToken ct)
    {
        // Build transition counts for drill-in visualization
        var transitionCounts = new Dictionary<string, int>();
        for (var i = 1; i < requests.Count; i++)
        {
            var key = $"{requests[i - 1].State}->{requests[i].State}";
            transitionCounts[key] = transitionCounts.GetValueOrDefault(key) + 1;
        }

        // Collect distinct templatized paths
        var paths = requests.Select(r => r.PathTemplate).Distinct().ToList();

        // Compute average bot probability from the request statuses
        var errorCount = requests.Count(r => r.StatusCode >= 400 && r.StatusCode < 500);

        var persisted = new PersistedSession
        {
            Signature = snapshot.Signature,
            StartedAt = snapshot.StartedAt.UtcDateTime,
            EndedAt = snapshot.EndedAt.UtcDateTime,
            RequestCount = snapshot.RequestCount,
            Vector = SqliteDetectionArchive.SerializeVector(snapshot.Vector),
            Maturity = snapshot.Maturity,
            DominantState = snapshot.DominantState.ToString(),
            IsBot = false, // Will be set by the contributor based on final detection
            AvgBotProbability = 0, // Filled by contributor
            AvgConfidence = 0,
            RiskBand = "Unknown",
            TransitionCountsJson = JsonSerializer.Serialize(transitionCounts, BotDetectionJsonSerializerContext.Default.DictionaryStringInt32),
            PathsJson = JsonSerializer.Serialize(paths, BotDetectionJsonSerializerContext.Default.ListString),
            ErrorCount = errorCount,
            TimingEntropy = ComputeTimingEntropy(requests),
            HeaderHashesJson = snapshot.HeaderHashesJson,
            FrequencyFingerprintBlob = snapshot.FrequencyFingerprint != null
                ? SqliteDetectionArchive.SerializeVector(snapshot.FrequencyFingerprint) : null,
            DriftVectorBlob = snapshot.DriftVector != null
                ? SqliteDetectionArchive.SerializeVector(snapshot.DriftVector) : null
        };

        // Background persister runs off the request pipeline; the SessionSnapshot
        // was built at request time but the scope wasn't captured on it. Pass
        // RequestScope.Unknown -- the pathfinder tasks (T14+) that persist
        // scope-owned rows extend SessionSnapshot with a scope carrier so this
        // caller can eventually thread the real (domain, host) through.
        _ = await _store.AddSessionAsync(RequestScope.Unknown, persisted, ct);

        // Upsert signature
        var sig = new PersistedSignature
        {
            SignatureId = snapshot.Signature,
            SessionCount = 1,
            TotalRequestCount = snapshot.RequestCount,
            FirstSeen = snapshot.StartedAt.UtcDateTime,
            LastSeen = snapshot.EndedAt.UtcDateTime,
            IsBot = false,
            BotProbability = 0,
            Confidence = 0,
            RiskBand = "Unknown",
            RootVector = SqliteDetectionArchive.SerializeVector(snapshot.Vector),
            RootVectorMaturity = snapshot.Maturity
        };

        await _store.UpsertSignatureAsync(RequestScope.Unknown, sig, ct);

        // Resolve entity - creates one if new, returns existing if known.
        // This is async/background so it doesn't block the request pipeline.
        try
        {
            var entityId = await _store.ResolveEntityAsync(snapshot.Signature, ct);
            _logger.LogDebug("Session {Signature} → entity {EntityId}",
                snapshot.Signature[..Math.Min(8, snapshot.Signature.Length)], entityId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Entity resolution failed for {Signature}", snapshot.Signature);
        }

        _logger.LogDebug(
            "Persisted session for {Signature}: {Requests} requests, {Transitions} transitions",
            snapshot.Signature, snapshot.RequestCount, transitionCounts.Count);
    }

    private static float ComputeTimingEntropy(IReadOnlyList<SessionRequest> requests)
    {
        if (requests.Count < 2) return 0;

        var intervals = new List<double>(requests.Count - 1);
        for (var i = 1; i < requests.Count; i++)
            intervals.Add((requests[i].Timestamp - requests[i - 1].Timestamp).TotalMilliseconds);

        var buckets = new Dictionary<int, int>();
        foreach (var interval in intervals)
        {
            var bucket = (int)(interval / 100);
            buckets[bucket] = buckets.GetValueOrDefault(bucket) + 1;
        }

        var total = (double)intervals.Count;
        double entropy = 0;
        foreach (var count in buckets.Values)
        {
            var p = count / total;
            if (p > 0) entropy -= p * Math.Log2(p);
        }

        return (float)entropy;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _sessionStore.SessionFinalized -= _onFinalizedHandler; }
        catch { /* event source may already be torn down */ }
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}

/// <summary>
///     Boot- and shutdown-time bookend for
///     <see cref="SessionPersistenceService"/>. On <see cref="StartAsync"/>
///     this initializes the underlying <see cref="IDetectionArchive"/> (SQLite
///     schema creation + cold-tier load) BEFORE the persistence service's
///     channel starts receiving session-finalize events. On
///     <see cref="StopAsync"/> it flushes any active in-memory sessions
///     (which fire SessionFinalized) then drains whatever landed on the
///     channel since the last tick -- mirrors the legacy
///     <c>BackgroundService.ExecuteAsync</c> shutdown-drain semantics.
///     <para>
///         Pattern matches the
///         <see cref="Mostlylucid.BotDetection.Storage.StoreInitService{TStore}"/>
///         style of one-shot boot initializers; promoted to its own type
///         because the persistence service has shutdown work in addition to
///         the initialisation work.
///     </para>
/// </summary>
internal sealed class SessionPersistenceLifecycleHostedService : IHostedService
{
    private readonly IDetectionArchive _store;
    private readonly SessionStore _sessionStore;
    private readonly SessionPersistenceService _persistence;
    private readonly ILogger<SessionPersistenceLifecycleHostedService> _logger;

    public SessionPersistenceLifecycleHostedService(
        IDetectionArchive store,
        SessionStore sessionStore,
        SessionPersistenceService persistence,
        ILogger<SessionPersistenceLifecycleHostedService> logger)
    {
        _store = store;
        _sessionStore = sessionStore;
        _persistence = persistence;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        _logger.LogInformation("Session persistence lifecycle started; store initialised");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Flush all active in-memory sessions first -- they fire SessionFinalized
        // and land on the persistence channel.
        await _sessionStore.FlushAllActiveSessionsAsync();

        // Then complete the writer + drain whatever landed.
        _persistence.CompleteWriter();
        await _persistence.DrainAndPersistAllAsync(CancellationToken.None);

        _logger.LogInformation("Session persistence service stopped; all pending sessions written");
    }
}