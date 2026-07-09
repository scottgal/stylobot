namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Category of invariant a guardian keeps. Guardians are a GLOBAL (FOSS-core)
///     concept; the category is how the dashboard groups them and how packs opt in.
/// </summary>
public enum GuardianCategory
{
    /// <summary>Bounds the underlying stores (retention, behavioural compaction, eviction).</summary>
    Data,

    /// <summary>Regulatory retention / drift / DSAR deadlines (commercial impls).</summary>
    Compliance,

    /// <summary>License / tamper integrity.</summary>
    License
}

/// <summary>
///     A periodic, self-scheduling worker that keeps one invariant true. The
///     framework is FOSS-core: <see cref="GuardianService"/> walks every registered
///     <see cref="IGuardian"/> on its own <see cref="Interval"/> and records the
///     latest <see cref="GuardianReport"/>. Data guardians (bounded storage),
///     compliance guardians (commercial), and license guardians all implement this
///     one contract; only the in-app editor for their config is a commercial surface.
/// </summary>
public interface IGuardian
{
    /// <summary>Stable identity, unique across guardians. Used as the roster key.</summary>
    string Name { get; }

    /// <summary>Which invariant class this guardian keeps.</summary>
    GuardianCategory Category { get; }

    /// <summary>How often the guardian should run. The walker gates per-guardian
    ///     on this, so a 6 h retention guardian and a 20 min data guardian coexist.</summary>
    TimeSpan Interval { get; }

    /// <summary>Whether this guardian runs. Disabled guardians stay on the roster
    ///     but are skipped by the walker. Defaults to true so existing guardians
    ///     keep compiling without change.</summary>
    bool Enabled => true;

    /// <summary>Do one pass. MUST be self-contained and cancellation-aware; the
    ///     walker catches and backs off on throw. Return a report even on a no-op.</summary>
    Task<GuardianReport> GuardAsync(CancellationToken ct = default);
}

/// <summary>Structured outcome of one guardian pass. Feeds the dashboard roster + count.</summary>
public sealed record GuardianReport
{
    public required string GuardianName { get; init; }
    public required GuardianCategory Category { get; init; }

    /// <summary>ok | compacted | evicted | pruned | alert | error.</summary>
    public required string Status { get; init; }

    /// <summary>Durable rows before this pass (0 when not applicable).</summary>
    public long RowsBefore { get; init; }

    /// <summary>Durable rows after this pass.</summary>
    public long RowsAfter { get; init; }

    /// <summary>Bytes reclaimed on the durable tier this pass.</summary>
    public long BytesReclaimed { get; init; }

    public double DurationMs { get; init; }
    public string? Details { get; init; }

    /// <summary>When the pass completed (stamped by the walker, not the guardian,
    ///     so guardians stay pure — no ambient clock).</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>Convenience: an "ok, nothing to do" report for a no-op pass.</summary>
    public static GuardianReport Ok(IGuardian g, double durationMs, long rows = 0) => new()
    {
        GuardianName = g.Name, Category = g.Category, Status = "ok",
        RowsBefore = rows, RowsAfter = rows, DurationMs = durationMs
    };
}
