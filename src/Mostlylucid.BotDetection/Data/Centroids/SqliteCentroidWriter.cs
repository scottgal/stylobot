using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Data.Centroids;

/// <summary>
/// Singleton drain that owns a bounded channel flushed by a single long-lived
/// loop holding ONE reused SQLite connection. Mirrors the
/// <c>SessionPersistenceAtom</c> pattern exactly.
/// </summary>
public sealed class SqliteCentroidWriter : ICentroidWriter, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteSessionCentroidStore _sessionStore;
    private readonly SqliteSignatureCentroidStore _signatureStore;
    private readonly SqliteIntentCentroidStore _intentStore;
    private readonly CentroidWriterOptions _options;
    private readonly ILogger<SqliteCentroidWriter> _logger;

    private readonly Channel<CentroidWriteMessage> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _drainer;

    private long _dropped;
    private int _disposed;

    // Per-connection state owned exclusively by DrainAsync.
    private SqliteConnection? _conn;

    public SqliteCentroidWriter(
        string connectionString,
        SqliteSessionCentroidStore sessionStore,
        SqliteSignatureCentroidStore signatureStore,
        SqliteIntentCentroidStore intentStore,
        IOptions<CentroidWriterOptions> options,
        ILogger<SqliteCentroidWriter> logger)
    {
        _connectionString = connectionString;
        _sessionStore     = sessionStore;
        _signatureStore   = signatureStore;
        _intentStore      = intentStore;
        _options          = options.Value;
        _logger           = logger;

        _channel = Channel.CreateBounded<CentroidWriteMessage>(
            new BoundedChannelOptions(_options.ChannelCapacity)
            {
                FullMode    = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        _cts     = new CancellationTokenSource();
        _drainer = Task.Run(() => DrainAsync(_cts.Token));
    }

    // ── ICentroidWriter ───────────────────────────────────────────────────────

    /// <summary>
    /// Non-blocking enqueue. DropOldest guarantees TryWrite always returns true;
    /// the increment is belt-and-braces for future channel-mode changes.
    /// </summary>
    public void Enqueue(CentroidWriteMessage message)
    {
        if (_disposed != 0) return;
        if (!_channel.Writer.TryWrite(message))
            Interlocked.Increment(ref _dropped);
    }

    public int QueueDepth  => _channel.Reader.Count;
    public long DroppedCount => Interlocked.Read(ref _dropped);

    // ── Drain loop ────────────────────────────────────────────────────────────

    private async Task DrainAsync(CancellationToken ct)
    {
        var lastDropLog   = DateTimeOffset.UtcNow;
        var lastDropCount = 0L;

        try
        {
            await foreach (var msg in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteOneAsync(msg, ct).ConfigureAwait(false);
                MaybeLogDrops(ref lastDropLog, ref lastDropCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown: drain whatever is still queued synchronously
            // so messages observed just before Dispose() are not silently dropped.
            while (_channel.Reader.TryRead(out var msg))
            {
                try
                {
                    // Use CancellationToken.None so the final flush is not cancelled.
                    await WriteOneAsync(msg, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Centroid write failed during shutdown drain, dropping message");
                }
            }
        }
        finally
        {
            DisposeConn();
        }
    }

    private async Task WriteOneAsync(CentroidWriteMessage msg, CancellationToken ct)
    {
        try
        {
            var conn = await GetConnAsync(ct).ConfigureAwait(false);
            switch (msg)
            {
                case CentroidWriteMessage.SessionCentroidWrite s:
                    await _sessionStore.UpsertSessionAsync(conn, s.Row, ct).ConfigureAwait(false);
                    break;
                case CentroidWriteMessage.SignatureCentroidWrite g:
                    await _signatureStore.UpsertSignatureAsync(conn, g.SignatureId, g.Vector, g.WasBot, g.Confidence, ct).ConfigureAwait(false);
                    break;
                case CentroidWriteMessage.IntentCentroidWrite i:
                    await _intentStore.UpsertIntentAsync(conn, i.SignatureId, i.Vector, i.ThreatScore, i.IntentCategory, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Let the loop handle cancellation.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Centroid write failed, dropping message and reconnecting");
            DisposeConn();
            try { await Task.Delay(_options.ReconnectBackoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* shutdown; let caller throw */ }
        }
    }

    /// <summary>
    /// Returns the shared connection, opening it lazily or after a failure.
    /// </summary>
    private async Task<SqliteConnection> GetConnAsync(CancellationToken ct)
    {
        if (_conn is not null && _conn.State == System.Data.ConnectionState.Open)
            return _conn;

        DisposeConn();
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        _conn = conn;
        return _conn;
    }

    private void DisposeConn()
    {
        var c = _conn;
        _conn = null;
        try { c?.Dispose(); } catch { /* best-effort */ }
    }

    private void MaybeLogDrops(ref DateTimeOffset lastLog, ref long lastCount)
    {
        var currentDropped = Interlocked.Read(ref _dropped);
        if (currentDropped == lastCount) return;
        var now = DateTimeOffset.UtcNow;
        if (now - lastLog < _options.DropLogCadence) return;

        _logger.LogWarning(
            "SqliteCentroidWriter dropped {Count} messages since last log (total {Total})",
            currentDropped - lastCount,
            currentDropped);

        lastLog   = now;
        lastCount = currentDropped;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _channel.Writer.TryComplete();
        _cts.Cancel();

        try { _drainer.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { /* OperationCanceledException expected */ }
        catch (Exception) { /* log nothing: Dispose must not throw */ }

        // Connection is disposed inside DrainAsync's finally block; belt-and-braces:
        DisposeConn();
        _cts.Dispose();
    }
}
