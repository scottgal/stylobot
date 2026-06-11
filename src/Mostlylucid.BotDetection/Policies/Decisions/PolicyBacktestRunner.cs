using System.Collections.Generic;
using System.Text.Json;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Pure projection of a candidate predicate over the decision log. Used
///     by the C8 backtest panel: given a predicate the operator is editing,
///     show how many of the requests in a window would have matched, how
///     many the candidate would have actually enforced (this rule wins),
///     how many would still pass because a higher-priority Allow won, and
///     a proxy for the latency the rule would have added.
///
///     <para>
///     "Higher-priority Allow wins" maps to the runtime semantics: the
///     evaluator stops at the first Live-mode rule whose predicate matches.
///     If the matching row's winner is already an Allow, the candidate
///     would be overridden because Allow short-circuits the rest of the
///     stack.
///     </para>
///
///     <para>
///     The runner is intentionally schema-tolerant: rows written before
///     C8's schema patch have a <c>null</c> SignalsSnapshot. Those rows
///     can't have the candidate predicate projected against them; the
///     runner counts them only in <see cref="PolicyBacktestResult.TotalRequests"/>
///     and surfaces a caveat string when at least one row was skipped.
///     </para>
/// </summary>
public sealed class PolicyBacktestRunner
{
    /// <summary>Cap on the number of sample fingerprints surfaced in the result.</summary>
    public const int SampleFingerprintCap = 25;

    /// <summary>Cap on the number of latency samples held in memory for percentile math.</summary>
    private const int LatencySampleCap = 8192;

    private readonly IPolicyDecisionLog _log;

    public PolicyBacktestRunner(IPolicyDecisionLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    ///     Project <paramref name="candidatePredicate"/> over every row in
    ///     the decision log within <paramref name="window"/>. Returns a
    ///     <see cref="PolicyBacktestResult"/> that buckets each row.
    /// </summary>
    /// <param name="candidatePredicate">The predicate the operator is editing.</param>
    /// <param name="window">Sliding window ending now (24h / 7d / 30d typical).</param>
    /// <param name="maxRows">Hard cap on rows scanned. Defaults to 100k.</param>
    /// <param name="ct">Cancellation token forwarded to the log stream.</param>
    public async Task<PolicyBacktestResult> RunAsync(
        Predicate.Predicate candidatePredicate,
        TimeSpan window,
        int maxRows = 100_000,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidatePredicate);

        var total = 0;
        var wouldMatch = 0;
        var wouldEnforce = 0;
        var overridden = 0;
        var inconclusive = 0;
        var sampleFps = new List<string>(capacity: SampleFingerprintCap);
        var sampleFpSet = new HashSet<string>(StringComparer.Ordinal);
        var latencies = new List<long>(capacity: 1024);

        // The decision log emits one row per rule per request. The runner
        // de-dups by (fingerprint, observed_at) so a single request only
        // counts once in TotalRequests / WouldMatch. The same key is the
        // unit the "higher-priority Allow won" check operates on.
        var seenRequests = new HashSet<(string fp, long observedAtMs)>();
        var requestState = new Dictionary<(string fp, long observedAtMs), RequestState>();

        await foreach (var row in _log.StreamWindowAsync(window, maxRows, ct).ConfigureAwait(false))
        {
            var key = (row.RequestFingerprint ?? string.Empty, row.ObservedAt.ToUnixTimeMilliseconds());

            if (seenRequests.Add(key))
            {
                total++;
                requestState[key] = new RequestState
                {
                    OriginalWinnerWasAllow = false,
                    CandidateMatched = null
                };
            }

            // Track latency samples regardless of match -- they're a proxy
            // for the cost of evaluating a predicate of similar shape.
            if (row.EvalLatencyTicks > 0 && latencies.Count < LatencySampleCap)
                latencies.Add(row.EvalLatencyTicks);

            // Did THIS row's original rule win as Allow? That's the
            // "higher-priority Allow short-circuited the stack" signal
            // for the candidate. Multiple rows for the same request share
            // the same winner, so OR-folding is safe.
            if (row.WinnerRuleId == row.RuleId
                && row.Matched
                && row.Action is PolicyAction.Allow)
            {
                requestState[key].OriginalWinnerWasAllow = true;
            }

            // Try to project the candidate predicate against this row's
            // signals. We only need to do this once per request key --
            // the snapshot is identical across rows from the same eval.
            if (requestState[key].CandidateMatched is null)
            {
                var projection = TryProject(candidatePredicate, row.SignalsSnapshot);
                if (projection is null)
                {
                    requestState[key].CandidateInconclusive = true;
                }
                else
                {
                    requestState[key].CandidateMatched = projection.Value;
                }
            }
        }

        // Bucket pass: walk the request states and translate into the
        // four counters.
        foreach (var (key, state) in requestState)
        {
            if (state.CandidateInconclusive && state.CandidateMatched is null)
            {
                inconclusive++;
                continue;
            }

            if (state.CandidateMatched is true)
            {
                wouldMatch++;
                if (state.OriginalWinnerWasAllow)
                    overridden++;
                else
                    wouldEnforce++;

                if (!string.IsNullOrEmpty(key.fp)
                    && sampleFps.Count < SampleFingerprintCap
                    && sampleFpSet.Add(key.fp))
                {
                    sampleFps.Add(key.fp);
                }
            }
        }

        latencies.Sort();
        var p50Ticks = Percentile(latencies, 0.50);
        var p99Ticks = Percentile(latencies, 0.99);

        string? caveat = null;
        if (inconclusive > 0)
        {
            caveat = inconclusive == total
                ? "No rows in this window carry a signals snapshot yet. Backtest is unavailable until C8 schema-patched rows accumulate."
                : $"{inconclusive:N0} of {total:N0} rows pre-date the C8 schema patch and were skipped (no signals snapshot).";
        }

        return new PolicyBacktestResult(
            TotalRequests: total,
            WouldMatch: wouldMatch,
            WouldEnforce: wouldEnforce,
            OverriddenByHigherPriority: overridden,
            EstimatedAddedP50Micros: TicksToMicros(p50Ticks),
            EstimatedAddedP99Micros: TicksToMicros(p99Ticks),
            SampleMatchingFingerprints: sampleFps,
            Window: window,
            ComputedAt: DateTimeOffset.UtcNow,
            Caveat: caveat);
    }

    /// <summary>
    ///     Deserialise the snapshot and run the candidate predicate against
    ///     it. Returns <c>null</c> when the snapshot is missing or malformed
    ///     -- the caller treats that as "inconclusive for this row".
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Snapshot deserialiser walks a flat object whose values are primitives / arrays only.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Snapshot deserialiser walks a flat object whose values are primitives / arrays only.")]
    private static bool? TryProject(Predicate.Predicate candidate, string? snapshotJson)
    {
        if (string.IsNullOrEmpty(snapshotJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var signals = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                signals[prop.Name] = JsonValueToObject(prop.Value);
            }

            return PredicateEvaluator.Evaluate(candidate, signals);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reduce a JsonElement back to the primitive shape
    ///     <see cref="PredicateEvaluator"/> understands. Strings, numbers
    ///     (as decimal -- matches the AST's JSON round-trip), booleans, and
    ///     arrays of strings are the load-bearing shapes. Nested objects
    ///     come back as the raw JSON text -- the evaluator treats those as
    ///     opaque strings, which matches its "silent miss on unknown facet
    ///     shape" semantics.
    /// </summary>
    private static object? JsonValueToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : (object)el.GetDouble(),
        JsonValueKind.Array => ToStringArray(el),
        JsonValueKind.Null => null,
        _ => el.GetRawText()
    };

    private static string[] ToStringArray(JsonElement el)
    {
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            list.Add(item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : item.GetRawText());
        return list.ToArray();
    }

    /// <summary>
    ///     Nearest-rank percentile on a sorted sample. Returns 0 for the
    ///     empty input -- matches <see cref="PolicyDecisionAggregate"/>'s
    ///     existing convention.
    /// </summary>
    private static long Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        var idx = (int)Math.Floor(p * sorted.Count);
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    /// <summary>10 TimeSpan ticks per microsecond.</summary>
    private static long TicksToMicros(long ticks) => ticks <= 0 ? 0 : ticks / 10L;

    /// <summary>
    ///     Per-request bucketing state captured during the streaming pass.
    ///     The runner needs to know two things per (fingerprint, observed_at)
    ///     evaluation: did any rule in this evaluation win as Allow (=>
    ///     candidate would be overridden), and does the candidate predicate
    ///     match the request's signal snapshot.
    /// </summary>
    private sealed class RequestState
    {
        public bool OriginalWinnerWasAllow { get; set; }
        public bool? CandidateMatched { get; set; }
        public bool CandidateInconclusive { get; set; }
    }
}
