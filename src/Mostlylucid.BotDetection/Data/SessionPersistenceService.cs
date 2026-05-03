using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Background service that persists completed sessions to the ISessionStore.
///     Listens to SessionStore.SessionFinalized events and writes asynchronously
///     via a bounded channel to avoid blocking the request pipeline.
/// </summary>
public sealed class SessionPersistenceService : BackgroundService
{
    private readonly ISessionStore _store;
    private readonly SessionStore _sessionStore;
    private readonly ILogger<SessionPersistenceService> _logger;
    private readonly Channel<(SessionSnapshot Snapshot, IReadOnlyList<SessionRequest> Requests)> _channel;

    public SessionPersistenceService(
        ISessionStore store,
        SessionStore sessionStore,
        ILogger<SessionPersistenceService> logger)
    {
        _store = store;
        _sessionStore = sessionStore;
        _logger = logger;
        _channel = Channel.CreateBounded<(SessionSnapshot, IReadOnlyList<SessionRequest>)>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken);

        // Subscribe to session finalization events
        _sessionStore.SessionFinalized += OnSessionFinalized;

        _logger.LogInformation("Session persistence service started");

        try
        {
            await foreach (var (snapshot, requests) in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await PersistSessionAsync(snapshot, requests, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to persist session for {Signature}", snapshot.Signature);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown signal received - fall through to drain active sessions
        }
        finally
        {
            _sessionStore.SessionFinalized -= OnSessionFinalized;
        }

        // Graceful shutdown: flush all active in-memory sessions first (they fire SessionFinalized),
        // then drain whatever landed in the channel. This ensures no sessions are silently dropped.
        await _sessionStore.FlushAllActiveSessionsAsync();

        _channel.Writer.TryComplete();
        await foreach (var (snapshot, requests) in _channel.Reader.ReadAllAsync())
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

        _logger.LogInformation("Session persistence service stopped; all pending sessions written");
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
            Vector = SqliteSessionStore.SerializeVector(snapshot.Vector),
            Maturity = snapshot.Maturity,
            DominantState = snapshot.DominantState.ToString(),
            IsBot = false, // Will be set by the contributor based on final detection
            AvgBotProbability = 0, // Filled by contributor
            AvgConfidence = 0,
            RiskBand = "Unknown",
            TransitionCountsJson = JsonSerializer.Serialize(transitionCounts),
            PathsJson = JsonSerializer.Serialize(paths),
            ErrorCount = errorCount,
            TimingEntropy = ComputeTimingEntropy(requests),
            HeaderHashesJson = snapshot.HeaderHashesJson,
            FrequencyFingerprintBlob = snapshot.FrequencyFingerprint != null
                ? SqliteSessionStore.SerializeVector(snapshot.FrequencyFingerprint) : null,
            DriftVectorBlob = snapshot.DriftVector != null
                ? SqliteSessionStore.SerializeVector(snapshot.DriftVector) : null
        };

        _ = await _store.AddSessionAsync(persisted, ct);

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
            RootVector = SqliteSessionStore.SerializeVector(snapshot.Vector),
            RootVectorMaturity = snapshot.Maturity
        };

        await _store.UpsertSignatureAsync(sig, ct);

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
}