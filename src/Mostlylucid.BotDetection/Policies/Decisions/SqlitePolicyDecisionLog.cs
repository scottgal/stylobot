using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Rules;

// The unqualified name "PolicyAction" is ambiguous inside the
// Mostlylucid.BotDetection.Policies tree:
//   * Mostlylucid.BotDetection.Policies.PolicyAction is a legacy enum used by
//     DetectionPolicy
//   * Mostlylucid.BotDetection.Policies.Rules.PolicyAction is the new abstract
//     record hierarchy we want here
// Parent-namespace lookup of the enclosing namespace wins over `using`
// imports, so we sidestep with a distinct alias name.
using RuleAction = Mostlylucid.BotDetection.Policies.Rules.PolicyAction;

namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     SQLite-backed write-behind <see cref="IPolicyDecisionLog"/>. The hot
///     path enqueues into a bounded channel; a single background drainer
///     batches up to <see cref="PolicyDecisionLogOptions.DrainerBatchSize"/>
///     rows per cycle into a transactional INSERT against the persistent
///     connection. Aggregation reads run direct SELECTs and pull latency
///     samples into memory for in-process percentile math (SQLite has no
///     native percentile).
/// </summary>
public sealed class SqlitePolicyDecisionLog : IPolicyDecisionLog, IAsyncDisposable
{
    /// <summary>Cap on the number of latency rows pulled into memory for percentile math, per aggregate call.</summary>
    private const int LatencySampleCap = 10_000;

    private readonly ILogger<SqlitePolicyDecisionLog> _logger;
    private readonly PolicyDecisionLogOptions _options;
    private readonly SqliteConnection _connection;
    private readonly Channel<PolicyDecision> _channel;
    private readonly CancellationTokenSource _drainerCts = new();
    private readonly Task _drainerTask;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    ///     Count of decisions dropped because the channel was full. Surfaced
    ///     for future metric registration; exposed as a field for now so the
    ///     metric registration in the gateway DI extension can plug it in
    ///     without breaking the API.
    /// </summary>
    private long _droppedCount;

    /// <summary>Number of rows the channel has dropped due to capacity pressure.</summary>
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <summary>
    ///     Convenience constructor used by tests and call sites that don't
    ///     care about retention / drainer tuning. Passing <c>":memory:"</c>
    ///     uses an in-memory database; the persistent connection keeps the
    ///     data alive for the lifetime of this instance.
    /// </summary>
    public SqlitePolicyDecisionLog(string databasePath)
        : this(
            Options.Create(new PolicyDecisionLogOptions { DatabasePath = databasePath }),
            NullLogger<SqlitePolicyDecisionLog>.Instance)
    {
    }

    public SqlitePolicyDecisionLog(
        IOptions<PolicyDecisionLogOptions> opts,
        ILogger<SqlitePolicyDecisionLog> logger)
    {
        _logger = logger;
        _options = opts.Value;

        var connectionString = BuildConnectionString(_options.DatabasePath);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        InitialiseSchema();

        _channel = Channel.CreateBounded<PolicyDecision>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _drainerTask = Task.Run(() => DrainerLoopAsync(_drainerCts.Token));
    }

    private static string BuildConnectionString(string databasePath)
    {
        // Treat :memory: explicitly so the persistent _connection keeps the in-memory
        // database alive across operations within the instance lifetime.
        if (string.Equals(databasePath, ":memory:", StringComparison.Ordinal))
            return "Data Source=:memory:";
        return $"Data Source={databasePath}";
    }

    private void InitialiseSchema()
    {
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = Mostlylucid.BotDetection.Data.Schema.SchemaLoader.Load("policy_decisions");
            cmd.ExecuteNonQuery();
        }

        ApplyForwardOnlyPatches();
    }

    /// <summary>
    ///     Apply every embedded <c>Policies/Decisions/Schema/*.sql</c> patch
    ///     in resource-name order. Each patch is a single ALTER TABLE that we
    ///     execute in isolation -- SQLite's pre-3.35 ALTER TABLE doesn't
    ///     support IF NOT EXISTS, so the loader treats a "duplicate column
    ///     name" failure as a no-op (the column was added on a previous
    ///     start). Anything else surfaces as a startup warning; the schema
    ///     stays usable because the patches only widen the table.
    /// </summary>
    private void ApplyForwardOnlyPatches()
    {
        var asm = typeof(SqlitePolicyDecisionLog).Assembly;
        const string prefix = "Mostlylucid.BotDetection.Policies.Decisions.Schema.";
        var names = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        foreach (var resourceName in names)
        {
            string sql;
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                sql = reader.ReadToEnd();
            }

            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (
                ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
            {
                // Patch already applied on a previous start. Idempotent no-op.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "PolicyDecisionLog schema patch {Resource} failed; column-add patches are forward-only so the table stays usable",
                    resourceName);
            }
        }
    }

    public ValueTask AppendAsync(PolicyDecision decision, CancellationToken ct = default)
    {
        if (!_channel.Writer.TryWrite(decision))
        {
            // DropOldest channels only fail TryWrite when the writer is completed
            // (e.g. during disposal). Bump the drop counter so we don't silently lose track.
            Interlocked.Increment(ref _droppedCount);
        }
        return ValueTask.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        // Spin until the channel is empty AND a no-op drain pass has committed.
        // We acquire _writeLock to fence against the drainer mid-batch so the
        // "channel empty" check we do after lock acquisition is authoritative.
        const int maxSpinIters = 200;
        for (var i = 0; i < maxSpinIters; i++)
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                if (_channel.Reader.Count == 0)
                {
                    // Channel is drained and no batch is mid-flight. Done.
                    return;
                }

                DrainBatch();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        _logger.LogWarning("PolicyDecisionLog flush did not converge within {Iters} iterations", maxSpinIters);
    }

    private async Task DrainerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_options.DrainerInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await _writeLock.WaitAsync(ct);
                try
                {
                    DrainBatch();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PolicyDecisionLog drainer cycle failed");
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            // Final drain on shutdown.
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                while (_channel.Reader.Count > 0) DrainBatch();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PolicyDecisionLog final drain failed");
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PolicyDecisionLog drainer terminated unexpectedly");
        }
    }

    private void DrainBatch()
    {
        if (_channel.Reader.Count == 0) return;

        var batch = new List<PolicyDecision>(Math.Min(_options.DrainerBatchSize, _channel.Reader.Count));
        while (batch.Count < _options.DrainerBatchSize && _channel.Reader.TryRead(out var item))
            batch.Add(item);

        if (batch.Count == 0) return;

        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO policy_decisions
                (rule_id, winner_rule_id, matched, fingerprint, action_kind, mode, eval_ticks, observed_at, signals_snapshot)
            VALUES
                (@rule_id, @winner_rule_id, @matched, @fingerprint, @action_kind, @mode, @eval_ticks, @observed_at, @signals_snapshot)
            """;

        var pRuleId = cmd.CreateParameter(); pRuleId.ParameterName = "@rule_id"; cmd.Parameters.Add(pRuleId);
        var pWinnerId = cmd.CreateParameter(); pWinnerId.ParameterName = "@winner_rule_id"; cmd.Parameters.Add(pWinnerId);
        var pMatched = cmd.CreateParameter(); pMatched.ParameterName = "@matched"; cmd.Parameters.Add(pMatched);
        var pFp = cmd.CreateParameter(); pFp.ParameterName = "@fingerprint"; cmd.Parameters.Add(pFp);
        var pAction = cmd.CreateParameter(); pAction.ParameterName = "@action_kind"; cmd.Parameters.Add(pAction);
        var pMode = cmd.CreateParameter(); pMode.ParameterName = "@mode"; cmd.Parameters.Add(pMode);
        var pTicks = cmd.CreateParameter(); pTicks.ParameterName = "@eval_ticks"; cmd.Parameters.Add(pTicks);
        var pAt = cmd.CreateParameter(); pAt.ParameterName = "@observed_at"; cmd.Parameters.Add(pAt);
        var pSnap = cmd.CreateParameter(); pSnap.ParameterName = "@signals_snapshot"; cmd.Parameters.Add(pSnap);

        foreach (var d in batch)
        {
            pRuleId.Value = d.RuleId.ToString("D");
            pWinnerId.Value = d.WinnerRuleId.ToString("D");
            pMatched.Value = d.Matched ? 1 : 0;
            pFp.Value = (object?)d.RequestFingerprint ?? DBNull.Value;
            pAction.Value = ActionKindName(d.Action);
            pMode.Value = d.Mode.ToString();
            pTicks.Value = d.EvalLatencyTicks;
            pAt.Value = d.ObservedAt.ToUnixTimeMilliseconds();
            pSnap.Value = (object?)d.SignalsSnapshot ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public async Task<PolicyDecisionAggregate> AggregateAsync(
        Guid ruleId,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var computedAt = DateTimeOffset.UtcNow;
        var since = (computedAt - window).ToUnixTimeMilliseconds();
        var ruleIdString = ruleId.ToString("D");

        await _writeLock.WaitAsync(ct);
        try
        {
            // Counts: matched and total.
            int matched, total;
            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COALESCE(SUM(matched), 0),
                        COUNT(*)
                    FROM policy_decisions
                    WHERE rule_id = @rule_id AND observed_at >= @since
                    """;
                cmd.Parameters.AddWithValue("@rule_id", ruleIdString);
                cmd.Parameters.AddWithValue("@since", since);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    matched = (int)r.GetInt64(0);
                    total = r.GetInt32(1);
                }
                else
                {
                    matched = 0;
                    total = 0;
                }
            }

            // Win distribution: rows where THIS rule won, grouped by action_kind.
            var winDist = new Dictionary<string, int>(StringComparer.Ordinal);
            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT action_kind, COUNT(*)
                    FROM policy_decisions
                    WHERE rule_id = @rule_id
                      AND winner_rule_id = @rule_id
                      AND observed_at >= @since
                    GROUP BY action_kind
                    """;
                cmd.Parameters.AddWithValue("@rule_id", ruleIdString);
                cmd.Parameters.AddWithValue("@since", since);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    winDist[r.GetString(0)] = r.GetInt32(1);
            }

            // Latency samples for percentile calc.
            var samples = new List<long>(Math.Min(total, LatencySampleCap));
            await using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT eval_ticks
                    FROM policy_decisions
                    WHERE rule_id = @rule_id AND observed_at >= @since
                    ORDER BY eval_ticks
                    LIMIT @cap
                    """;
                cmd.Parameters.AddWithValue("@rule_id", ruleIdString);
                cmd.Parameters.AddWithValue("@since", since);
                cmd.Parameters.AddWithValue("@cap", LatencySampleCap);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    samples.Add(r.GetInt64(0));
            }

            var p50Ticks = Percentile(samples, 0.50);
            var p99Ticks = Percentile(samples, 0.99);

            return new PolicyDecisionAggregate(
                RuleId: ruleId,
                Window: window,
                Matched: matched,
                TotalEvaluations: total,
                WinDistribution: winDist,
                P50LatencyMicros: TicksToMicros(p50Ticks),
                P99LatencyMicros: TicksToMicros(p99Ticks),
                ComputedAt: computedAt);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<PolicyDecision>> GetByFingerprintAsync(
        string fingerprint,
        int max = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprint)) return Array.Empty<PolicyDecision>();

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT rule_id, winner_rule_id, matched, fingerprint, action_kind, mode, eval_ticks, observed_at, signals_snapshot
                FROM policy_decisions
                WHERE fingerprint = @fp
                ORDER BY observed_at
                LIMIT @max
                """;
            cmd.Parameters.AddWithValue("@fp", fingerprint);
            cmd.Parameters.AddWithValue("@max", max);

            var rows = new List<PolicyDecision>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(MapRow(r));
            }
            return rows;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Stream every row observed within <paramref name="window"/> ending
    ///     now, ordered ascending. Powers C8's
    ///     <c>PolicyBacktestRunner</c>: the runner projects a candidate
    ///     predicate against each row's <see cref="PolicyDecision.SignalsSnapshot"/>
    ///     without buffering the entire window. The reader opens a fresh
    ///     connection so the streaming enumeration doesn't fence the
    ///     write-lock-protected drainer; the trade-off is that very recent
    ///     un-drained rows (still in the channel buffer) are NOT visible
    ///     until the drainer flushes them. Tests call <see cref="FlushAsync"/>
    ///     before streaming.
    /// </summary>
    public async IAsyncEnumerable<PolicyDecision> StreamWindowAsync(
        TimeSpan window,
        int maxRows = 100_000,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        var since = cutoff.ToUnixTimeMilliseconds();

        // Acquire the write-lock so we don't race the drainer. We can't
        // safely stream across a yield while holding it though, so we
        // collect into an in-memory list under the lock, then iterate
        // outside. The maxRows cap (100k) makes the buffer footprint
        // bounded and the SELECT itself is index-backed.
        await _writeLock.WaitAsync(ct);
        List<PolicyDecision> rows;
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT rule_id, winner_rule_id, matched, fingerprint, action_kind, mode, eval_ticks, observed_at, signals_snapshot
                FROM policy_decisions
                WHERE observed_at >= @since
                ORDER BY observed_at
                LIMIT @max
                """;
            cmd.Parameters.AddWithValue("@since", since);
            cmd.Parameters.AddWithValue("@max", maxRows);

            rows = new List<PolicyDecision>(capacity: 1024);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(MapRow(r));
            }
        }
        finally
        {
            _writeLock.Release();
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    /// <summary>
    ///     Map the SELECT layout shared by <see cref="GetByFingerprintAsync"/>
    ///     and <see cref="StreamWindowAsync"/> into a <see cref="PolicyDecision"/>.
    ///     The two callers MUST keep their SELECT columns aligned with this
    ///     ordering -- only that one place hard-codes the indexes.
    /// </summary>
    private static PolicyDecision MapRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var ruleId = Guid.Parse(r.GetString(0));
        var winnerId = Guid.Parse(r.GetString(1));
        var matched = r.GetInt32(2) != 0;
        var fp = r.IsDBNull(3) ? null : r.GetString(3);
        var actionKind = r.GetString(4);
        var mode = Enum.Parse<PolicyMode>(r.GetString(5));
        var ticks = r.GetInt64(6);
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7));
        var snapshot = r.IsDBNull(8) ? null : r.GetString(8);

        return new PolicyDecision(
            RuleId: ruleId,
            WinnerRuleId: winnerId,
            Matched: matched,
            RequestFingerprint: fp,
            Action: ActionFromKind(actionKind),
            Mode: mode,
            EvalLatencyTicks: ticks,
            ObservedAt: observedAt,
            SignalsSnapshot: snapshot);
    }

    /// <summary>
    ///     Map a <see cref="PolicyAction"/> subtype to the lowercase verb the
    ///     dashboard surfaces. Centralised so the writer (INSERT) and the
    ///     reader (GetByFingerprintAsync) use the same vocabulary.
    /// </summary>
    internal static string ActionKindName(RuleAction action) => action switch
    {
        RuleAction.Allow => "allow",
        RuleAction.Observe => "observe",
        RuleAction.Tag => "tag",
        RuleAction.Challenge => "challenge",
        RuleAction.RateLimit => "ratelimit",
        RuleAction.Block => "block",
        _ => action.GetType().Name.ToLowerInvariant(),
    };

    /// <summary>
    ///     Reconstruct a placeholder <see cref="PolicyAction"/> from the stored
    ///     verb. The decision log persists Kind only; payload (challenge kind,
    ///     tag name, rate-limit budget) is not reconstructible from this row
    ///     -- the explainer joins back to <see cref="PolicyRule.Action"/> on
    ///     <see cref="PolicyDecision.RuleId"/> for the live shape.
    /// </summary>
    private static RuleAction ActionFromKind(string kind) => kind switch
    {
        "allow" => new RuleAction.Allow(),
        "observe" => new RuleAction.Observe(),
        "tag" => new RuleAction.Tag(string.Empty),
        "challenge" => new RuleAction.Challenge(string.Empty),
        "ratelimit" => new RuleAction.RateLimit(0),
        "block" => new RuleAction.Block(),
        _ => new RuleAction.Allow(),
    };

    private static long Percentile(IReadOnlyList<long> sortedSamples, double p)
    {
        if (sortedSamples.Count == 0) return 0;
        if (sortedSamples.Count == 1) return sortedSamples[0];
        var idx = (int)Math.Ceiling(p * sortedSamples.Count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sortedSamples.Count) idx = sortedSamples.Count - 1;
        return sortedSamples[idx];
    }

    private static long TicksToMicros(long ticks)
    {
        if (ticks <= 0) return 0;
        // EvalLatencyTicks is recorded by the PolicyEvaluator in TimeSpan tick
        // units (100ns per tick), captured via Stopwatch.GetElapsedTime().Ticks
        // so the value is platform-independent regardless of whether the
        // underlying Stopwatch is high-resolution or not. Convert to microseconds
        // by dividing by 10 (10 TimeSpan ticks per microsecond).
        return ticks / 10L;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        _drainerCts.Cancel();

        try { await _drainerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex) { _logger.LogDebug(ex, "PolicyDecisionLog drainer dispose threw"); }

        _drainerCts.Dispose();
        _connection.Close();
        _connection.Dispose();
        _writeLock.Dispose();
    }
}
