using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Domains;
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
    private readonly IDetectionArchive _store;
    private readonly ILogger<RequestPersistenceService> _logger;

    // Per-signature write-count state for LFU sampling decisions
    private readonly ConcurrentDictionary<string, SignatureWriteState> _writeState = new();

    // Accumulates individual requests; flushes when full (50) or every 5 ms
    private readonly BatchingAtom<PersistedRequest> _batcher;

    // Tracks in-flight batch count for backpressure sampling
    private int _pendingBatches;

    // Counters for soak observability. Categorise each ShouldWrite decision so a
    // load test can confirm the sampler tiers actually engage under sustained pressure.
    private long _writtenAlways;
    private long _writtenSampledIn;
    private long _droppedSampledOut;

    public RequestPersistenceService(
        IDetectionArchive store,
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

    /// <summary>Size of the LFU sampling state dictionary. Bounded near 50K by eviction.</summary>
    public int WriteStateCount => _writeState.Count;

    /// <summary>Requests written because they were bots or below the backpressure threshold.</summary>
    public long WrittenAlwaysCount => Interlocked.Read(ref _writtenAlways);

    /// <summary>Low-risk requests that survived LFU sampling under backpressure.</summary>
    public long WrittenSampledInCount => Interlocked.Read(ref _writtenSampledIn);

    /// <summary>Low-risk requests dropped by LFU sampling under backpressure.</summary>
    public long DroppedSampledOutCount => Interlocked.Read(ref _droppedSampledOut);

    /// <summary>
    ///     Enqueue a single request for persistence. Returns immediately.
    ///     Sampling policy:
    ///     - botProbability > 0.7 : always write
    ///     - in-flight batches &lt; 5  : always write
    ///     - in-flight batches 5-19  : write 1-in-3 low-risk requests
    ///     - in-flight batches 20+   : write 1-in-10 low-risk requests
    ///     <para>
    ///         <paramref name="scope"/> stamps the request row with the (domain,
    ///         host) that owned the request. Default <see cref="RequestScope.Unknown"/>
    ///         keeps legacy callers working; new callers should thread the scope
    ///         from <c>HttpContext.Items[HttpContextItemKeys.RequestScope]</c>.
    ///     </para>
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
        DateTime timestamp,
        RequestScope scope = default)
    {
        if (!ShouldWrite(signature, botProbability))
            return Task.CompletedTask;

        // default(RequestScope) is (null, null); RequestScope.Unknown is ("unknown","unknown").
        // Normalise so an omitted scope produces a real (Domain, Host) pair.
        var effective = string.IsNullOrEmpty(scope.Domain) && string.IsNullOrEmpty(scope.Host)
            ? RequestScope.Unknown
            : scope;

        _batcher.Enqueue(new PersistedRequest
        {
            Domain         = effective.Domain,
            Host           = effective.Host,
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
            // Each PersistedRequest in the batch already carries its own
            // (Domain, Host); the batch-level scope is a fallback for callers
            // that haven't threaded scope through yet. Pass Unknown here so
            // the store falls back only when the per-row values are null.
            await _store.AddRequestBatchAsync(RequestScope.Unknown, batch, ct).ConfigureAwait(false);
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
        if (botProbability > 0.7)
        {
            Interlocked.Increment(ref _writtenAlways);
            return true;
        }

        var pending = _pendingBatches;

        if (pending < 5)
        {
            Interlocked.Increment(ref _writtenAlways);
            return true;
        }

        var modulo = pending < 20 ? 3 : 10;
        if (SampleRequest(signature, modulo))
        {
            Interlocked.Increment(ref _writtenSampledIn);
            return true;
        }

        Interlocked.Increment(ref _droppedSampledOut);
        return false;
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
