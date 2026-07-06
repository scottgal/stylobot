namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     A lower-resolution projection of a finalised
///     <see cref="SessionAggregate"/>. One row per session that reaches TTL /
///     pressure eviction. Always written — this is the session accounting
///     trail so operators never lose track of "session S happened, hit these
///     endpoints, ended for reason R" regardless of whether the session had
///     enough signal to justify richer detail (Markov vector, full request
///     history, etc.) landing in a later enrichment pass.
/// </summary>
/// <remarks>
///     <para>
///         Every field comes off the aggregate that
///         <see cref="SessionFinalizingSignal"/> carried; no new state on the
///         hot path. Kept intentionally small so the write-behind channel
///         backing echoes stays cheap even at maximum load. Under back-pressure
///         the shed happens at the store level (higher-priority evictions
///         still emit) — the echo write itself does not gate.
///     </para>
///     <para>
///         See <c>reference_session_layer_and_fingerprint_levels</c> for the
///         behavioural priority function <see cref="RetentionPriority"/> is
///         computed against. The echo carries the priority so an operator
///         inspecting the archive can see which slice of the retention curve
///         each row came from.
///     </para>
/// </remarks>
public sealed record SessionEcho
{
    public required string FingerprintId { get; init; }
    public required string SiteId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int SampleCount { get; init; }
    public required double MeanBotProbability { get; init; }
    public required double MaxBotProbability { get; init; }
    public required double LatestConfidence { get; init; }
    public required int HoneypotHits { get; init; }

    /// <summary>Serialised as a small dictionary; empty on sessions that never hit upstream 4xx/5xx.</summary>
    public required IReadOnlyDictionary<int, int> UpstreamStatusCounts { get; init; }

    /// <summary>Dominant inferred client type across the session window (null if never inferred).</summary>
    public string? DominantClientType { get; init; }

    /// <summary>Behavioural retention priority at the time of finalization.</summary>
    public required double RetentionPriority { get; init; }

    /// <summary>Why the session was finalized (age-out, pressure, explicit, max-lifetime).</summary>
    public required SessionFinalizeReason FinalizeReason { get; init; }

    /// <summary>When the echo was materialised (== when finalization completed).</summary>
    public required DateTimeOffset EchoedAt { get; init; }

    /// <summary>Build a fresh echo from an aggregate + the finalizing signal that carried it.</summary>
    public static SessionEcho From(SessionFinalizingSignal signal, DateTimeOffset echoedAt)
    {
        var a = signal.Aggregate;
        return new SessionEcho
        {
            FingerprintId = a.FingerprintId,
            SiteId = a.SiteId,
            StartedAt = a.FirstSample,
            EndedAt = a.LastSample,
            SampleCount = a.SampleCount,
            MeanBotProbability = a.MeanBotProbability,
            MaxBotProbability = a.MaxBotProbability,
            LatestConfidence = a.LatestConfidence,
            HoneypotHits = a.HoneypotHits,
            UpstreamStatusCounts = a.UpstreamStatusCounts,
            DominantClientType = a.DominantClientType,
            RetentionPriority = a.RetentionPriority,
            FinalizeReason = signal.Reason,
            EchoedAt = echoedAt,
        };
    }
}

/// <summary>
///     Storage sink for finalised <see cref="SessionEcho"/> rows. FOSS ships a
///     Null default so hosts without a durable archive stay wired end-to-end
///     (echo build + ack still fires — nothing is silently dropped, the row
///     just doesn't survive the process). Commercial packs replace via
///     TryAdd-loses with a Postgres implementation. The FOSS SQLite archive
///     (next commit) also replaces.
/// </summary>
public interface ISessionEchoStore
{
    /// <summary>
    ///     Persist an echo. Called from <see cref="SessionEchoAtom"/> off the
    ///     lifecycle-signal hot path — this is expected to be a small, fast
    ///     write (single row, no fanout). The atom acks the finalizing signal
    ///     once this returns; slow implementations delay the two-phase
    ///     eviction of the aggregate for the session in question.
    /// </summary>
    Task AddEchoAsync(SessionEcho echo, CancellationToken cancellationToken = default);
}

/// <summary>No-op default. Rejects nothing, persists nothing.</summary>
public sealed class NullSessionEchoStore : ISessionEchoStore
{
    public Task AddEchoAsync(SessionEcho echo, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}