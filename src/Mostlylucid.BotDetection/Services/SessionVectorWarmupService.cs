using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Seeds the session HNSW index from SQLite on first startup.
///     Runs once if the index is empty (no serialized graph files found on disk).
///     Subsequent startups skip warmup — the HNSW graph is loaded from file in under a second.
/// </summary>
public sealed class SessionVectorWarmupService : BackgroundService
{
    private readonly ISessionStore _store;
    private readonly ISessionVectorSearch _vectorSearch;
    private readonly ILogger<SessionVectorWarmupService> _logger;

    private const int WarmupBatchSize = 5000;

    public SessionVectorWarmupService(
        ISessionStore store,
        ISessionVectorSearch vectorSearch,
        ILogger<SessionVectorWarmupService> logger)
    {
        _store = store;
        _vectorSearch = vectorSearch;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for HNSW LoadAsync (fires on startup as a background task) to complete
        try { await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        if (_vectorSearch.Count > 0)
        {
            _logger.LogDebug(
                "Session HNSW index already has {Count} vectors — skipping warmup",
                _vectorSearch.Count);
            return;
        }

        _logger.LogInformation(
            "Session HNSW index is empty — seeding from SQLite (up to {Batch} recent sessions)",
            WarmupBatchSize);

        try
        {
            var sessions = await _store.GetRecentSessionsAsync(WarmupBatchSize, null, stoppingToken);
            var added = 0;
            foreach (var session in sessions)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (session.Vector is not { Length: > 0 }) continue;

                var vector = SqliteSessionStore.DeserializeVector(session.Vector);
                if (vector == null) continue;

                var freqFp = SqliteSessionStore.DeserializeVector(session.FrequencyFingerprintBlob);
                var driftVec = SqliteSessionStore.DeserializeVector(session.DriftVectorBlob);

                await _vectorSearch.AddAsync(vector, session.Signature, session.IsBot, session.AvgBotProbability,
                    frequencyFingerprint: freqFp, driftVector: driftVec);
                added++;
            }

            if (added > 0)
            {
                await _vectorSearch.SaveAsync();
                _logger.LogInformation("Session HNSW warmup complete: {Count} sessions indexed", added);
            }
            else
            {
                _logger.LogDebug("No sessions with vectors found in SQLite; HNSW index stays empty until traffic arrives");
            }
        }
        catch (OperationCanceledException) { /* shutdown during warmup — fine */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session HNSW warmup failed; index will build incrementally from live traffic");
        }
    }
}
