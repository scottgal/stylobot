namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     FOSS guardian-roster view model: every registered <c>IGuardian</c> with its
///     latest report. Guardians are a global (FOSS-core) concept; this surface is
///     read-only. The in-app editor for intervals/caps/retention is commercial;
///     FOSS tunes the same values via appsettings.json.
/// </summary>
public sealed class GuardianRosterModel
{
    public string BasePath { get; init; } = "";

    /// <summary>False when no guardian runtime is registered (remote-mode dashboard host).</summary>
    public bool Available { get; init; }

    public IReadOnlyList<GuardianRosterRow> Rows { get; init; } = [];

    public int TotalGuardians => Rows.Count;
    public long TotalBytesReclaimed => Rows.Sum(r => r.BytesReclaimed);
    public bool AnyErrors => Rows.Any(r => r.Status == "error");
}

/// <summary>One guardian's identity + latest pass outcome.</summary>
public sealed class GuardianRosterRow
{
    public required string Name { get; init; }

    /// <summary>Data | Compliance | License.</summary>
    public required string Category { get; init; }

    /// <summary>Human interval, e.g. "30m", "6h".</summary>
    public required string IntervalDisplay { get; init; }

    /// <summary>ok | compacted | evicted | pruned | alert | error; null = not run yet.</summary>
    public string? Status { get; init; }

    public long RowsBefore { get; init; }
    public long RowsAfter { get; init; }
    public long BytesReclaimed { get; init; }
    public double DurationMs { get; init; }
    public string? Details { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}
