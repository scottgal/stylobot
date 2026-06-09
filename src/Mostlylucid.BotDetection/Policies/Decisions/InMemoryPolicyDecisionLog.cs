using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Lock-guarded in-process <see cref="IPolicyDecisionLog"/> for tests and
///     dev scenarios that don't want a SQLite file on disk. Same surface as
///     <see cref="SqlitePolicyDecisionLog"/> but storage is a single
///     <see cref="List{T}"/> behind a monitor lock -- no channel, no drainer,
///     no transactions.
/// </summary>
public sealed class InMemoryPolicyDecisionLog : IPolicyDecisionLog
{
    private readonly object _lock = new();
    private readonly List<PolicyDecision> _rows = new();

    public ValueTask AppendAsync(PolicyDecision decision, CancellationToken ct = default)
    {
        lock (_lock) _rows.Add(decision);
        return ValueTask.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Test helper: returns a defensive copy of every decision appended so
    ///     far. Useful for asserting that the evaluator wrote the expected
    ///     trace without round-tripping through the aggregate or fingerprint
    ///     query paths.
    /// </summary>
    public IReadOnlyList<PolicyDecision> Snapshot()
    {
        lock (_lock) return _rows.ToList();
    }

    public Task<PolicyDecisionAggregate> AggregateAsync(
        Guid ruleId,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var computedAt = DateTimeOffset.UtcNow;
        var since = computedAt - window;

        PolicyDecision[] snapshot;
        lock (_lock) snapshot = _rows.ToArray();

        var inWindow = snapshot
            .Where(r => r.RuleId == ruleId && r.ObservedAt >= since)
            .ToArray();

        var matched = inWindow.Count(r => r.Matched);
        var total = inWindow.Length;

        var winDist = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in inWindow.Where(r => r.WinnerRuleId == ruleId))
        {
            var kind = SqlitePolicyDecisionLog.ActionKindName(row.Action);
            winDist[kind] = winDist.TryGetValue(kind, out var n) ? n + 1 : 1;
        }

        var sortedTicks = inWindow.Select(r => r.EvalLatencyTicks).OrderBy(t => t).ToArray();
        var p50 = TicksToMicros(Percentile(sortedTicks, 0.50));
        var p99 = TicksToMicros(Percentile(sortedTicks, 0.99));

        return Task.FromResult(new PolicyDecisionAggregate(
            RuleId: ruleId,
            Window: window,
            Matched: matched,
            TotalEvaluations: total,
            WinDistribution: winDist,
            P50LatencyMicros: p50,
            P99LatencyMicros: p99,
            ComputedAt: computedAt));
    }

    public Task<IReadOnlyList<PolicyDecision>> GetByFingerprintAsync(
        string fingerprint,
        int max = 100,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return Task.FromResult<IReadOnlyList<PolicyDecision>>(Array.Empty<PolicyDecision>());

        PolicyDecision[] snapshot;
        lock (_lock) snapshot = _rows.ToArray();

        IReadOnlyList<PolicyDecision> rows = snapshot
            .Where(r => string.Equals(r.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            .OrderBy(r => r.ObservedAt)
            .Take(max)
            .ToArray();

        return Task.FromResult(rows);
    }

    /// <summary>
    ///     Snapshot the list under the lock, filter to the window, then
    ///     yield. Matches the SQLite impl's behaviour: ascending observed_at
    ///     ordering, capped at <paramref name="maxRows"/>.
    /// </summary>
    public async IAsyncEnumerable<PolicyDecision> StreamWindowAsync(
        TimeSpan window,
        int maxRows = 100_000,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - window;

        PolicyDecision[] snapshot;
        lock (_lock) snapshot = _rows.ToArray();

        var ordered = snapshot
            .Where(r => r.ObservedAt >= cutoff)
            .OrderBy(r => r.ObservedAt)
            .Take(maxRows);

        foreach (var row in ordered)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }

        await Task.CompletedTask;
    }

    private static long Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    private static long TicksToMicros(long ticks)
    {
        // EvalLatencyTicks is TimeSpan-ticks (100ns each). 10 ticks per microsecond.
        if (ticks <= 0) return 0;
        return ticks / 10L;
    }
}
