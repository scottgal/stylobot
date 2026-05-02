using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Singleton service that owns the write path for per-request SQLite persistence.
///     Uses a single-writer EphemeralWorkCoordinator to avoid SQLite file-level lock contention.
///     Applies LFU-sampling under load to protect throughput while ensuring bot requests
///     (probability > 0.7) are ALWAYS written regardless of queue depth.
/// </summary>
public sealed class RequestPersistenceService : IAsyncDisposable
{
    // Per-signature write-count state for LFU sampling decisions
    private readonly ConcurrentDictionary<string, SignatureWriteState> _writeState = new();

    // Single-writer coordinator: MaxConcurrency=1 prevents SQLite lock contention
    private readonly EphemeralWorkCoordinator<RequestBatch> _coordinator;

    private readonly ILogger<RequestPersistenceService> _logger;

    public RequestPersistenceService(
        ISessionStore store,
        ILogger<RequestPersistenceService> logger)
    {
        _logger = logger;

        _coordinator = new EphemeralWorkCoordinator<RequestBatch>(
            async (batch, ct) =>
            {
                try
                {
                    await store.AddRequestBatchAsync(batch.Requests, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "RequestPersistenceService: batch write failed ({Count} requests)",
                        batch.Requests.Count);
                }
            },
            new EphemeralOptions
            {
                MaxConcurrency = 1,          // Single writer — no SQLite file-lock races
                MaxTrackedOperations = 500
            });
    }

    /// <summary>Number of batches currently queued but not yet written.</summary>
    public int PendingWrites => _coordinator.PendingCount;

    /// <summary>
    ///     Enqueue a single request for persistence. Returns immediately; write is async.
    ///     Sampling policy:
    ///     - botProbability > 0.7 : always write
    ///     - queue depth &lt; 50  : always write
    ///     - queue depth 50-199  : write 1-in-3 low-risk requests
    ///     - queue depth 200+    : write 1-in-10 low-risk requests
    /// </summary>
    public async Task EnqueueAsync(
        string signature,
        string path,
        string markovState,
        int statusCode,
        double botProbability,
        double confidence,
        string riskBand,
        double processingMs,
        DateTime timestamp)
    {
        if (!ShouldWrite(signature, botProbability))
            return;

        var request = new PersistedRequest
        {
            Signature   = signature,
            Path        = path,
            MarkovState = markovState,
            StatusCode  = statusCode,
            BotProbability = botProbability,
            Confidence  = confidence,
            RiskBand    = riskBand,
            ProcessingMs = processingMs,
            Timestamp   = timestamp
        };

        var batch = new RequestBatch([request]);

        try
        {
            await _coordinator.EnqueueAsync(batch).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Coordinator was completed during dispose — drop silently
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Signal no more items, then drain what is queued
        _coordinator.Complete();

        try
        {
            await _coordinator.DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected if the app shuts down before the queue drains
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RequestPersistenceService: drain on dispose encountered an error");
        }

        await _coordinator.DisposeAsync().ConfigureAwait(false);
    }

    // --- sampling ---

    private bool ShouldWrite(string signature, double botProbability)
    {
        // Bot requests are always written — they are the primary signal
        if (botProbability > 0.7)
            return true;

        var pending = _coordinator.PendingCount;

        // Normal load: write everything
        if (pending < 50)
            return true;

        // Moderate load: 1-in-3
        if (pending < 200)
            return SampleRequest(signature, modulo: 3);

        // Heavy load: 1-in-10
        return SampleRequest(signature, modulo: 10);
    }

    private bool SampleRequest(string signature, int modulo)
    {
        if (_writeState.Count > 50_000)
            _writeState.Clear();
        var state = _writeState.GetOrAdd(signature, _ => new SignatureWriteState());
        var writeCount = Interlocked.Increment(ref state.WriteCount) & int.MaxValue;
        return writeCount % modulo == 0;
    }

    // --- inner types ---

    private sealed class SignatureWriteState
    {
        public int WriteCount;
    }

    private readonly record struct RequestBatch(IReadOnlyList<PersistedRequest> Requests);
}
