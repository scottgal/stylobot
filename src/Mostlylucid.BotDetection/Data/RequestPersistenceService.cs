using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.Ephemeral.Atoms.Batching;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Singleton service that owns the write path for per-request SQLite persistence.
///     Uses <see cref="BatchingAtom{T}" /> to coalesce up to 50 requests per flush
///     (or every 5 ms), reducing SQLite write frequency by ~50x under load.
///     Applies priority sampling: bot requests (probability > 0.7) are ALWAYS written;
///     low-risk requests are sampled down when the in-flight batch count is high.
/// </summary>
public sealed class RequestPersistenceService : IAsyncDisposable
{
    private readonly ISessionStore _store;
    private readonly ILogger<RequestPersistenceService> _logger;

    // Per-signature write-count state for LFU sampling decisions
    private readonly ConcurrentDictionary<string, SignatureWriteState> _writeState = new();

    // Accumulates individual requests; flushes when full (50) or every 5 ms
    private readonly BatchingAtom<PersistedRequest> _batcher;

    // Tracks in-flight batch count for backpressure sampling
    private int _pendingBatches;

    public RequestPersistenceService(
        ISessionStore store,
        ILogger<RequestPersistenceService> logger)
    {
        _store = store;
        _logger = logger;

        _batcher = new BatchingAtom<PersistedRequest>(
            onBatch: FlushBatchAsync,
            maxBatchSize: 50,
            flushInterval: TimeSpan.FromMilliseconds(5));
    }

    /// <summary>Number of batches currently in-flight (writing to SQLite).</summary>
    public int PendingWrites => _pendingBatches;

    /// <summary>
    ///     Enqueue a single request for persistence. Returns immediately.
    ///     Sampling policy:
    ///     - botProbability > 0.7 : always write
    ///     - in-flight batches &lt; 5  : always write
    ///     - in-flight batches 5-19  : write 1-in-3 low-risk requests
    ///     - in-flight batches 20+   : write 1-in-10 low-risk requests
    /// </summary>
    public Task EnqueueAsync(
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
            return Task.CompletedTask;

        _batcher.Enqueue(new PersistedRequest
        {
            Signature      = signature,
            Path           = path,
            MarkovState    = markovState,
            StatusCode     = statusCode,
            BotProbability = botProbability,
            Confidence     = confidence,
            RiskBand       = riskBand,
            ProcessingMs   = processingMs,
            Timestamp      = timestamp
        });

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync().ConfigureAwait(false);
    }

    // --- batch writer ---

    private async Task FlushBatchAsync(IReadOnlyList<PersistedRequest> batch, CancellationToken ct)
    {
        Interlocked.Increment(ref _pendingBatches);
        try
        {
            await _store.AddRequestBatchAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "RequestPersistenceService: batch write failed ({Count} requests)",
                batch.Count);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingBatches);
        }
    }

    // --- sampling ---

    private bool ShouldWrite(string signature, double botProbability)
    {
        // Bot requests are always written — they are the primary signal
        if (botProbability > 0.7)
            return true;

        var pending = _pendingBatches;

        if (pending < 5)
            return true;

        if (pending < 20)
            return SampleRequest(signature, modulo: 3);

        return SampleRequest(signature, modulo: 10);
    }

    private bool SampleRequest(string signature, int modulo)
    {
        if (_writeState.Count > 50_000)
        {
            foreach (var key in _writeState.Keys.Take(5_000))
                _writeState.TryRemove(key, out _);
        }
        var state = _writeState.GetOrAdd(signature, _ => new SignatureWriteState());
        var writeCount = Interlocked.Increment(ref state.WriteCount) & int.MaxValue;
        return writeCount % modulo == 0;
    }

    // --- inner types ---

    private sealed class SignatureWriteState
    {
        public int WriteCount;
    }
}
