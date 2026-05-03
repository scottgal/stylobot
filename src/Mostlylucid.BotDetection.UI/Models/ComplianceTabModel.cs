namespace Mostlylucid.BotDetection.UI.Models;

public sealed class ComplianceTabModel
{
    public required string ActivePackId { get; init; }
    public required string ActivePackName { get; init; }
    public string? ActivePackExplain { get; init; }
    public IReadOnlyList<string>? LegalReferences { get; init; }
    public string? Jurisdiction { get; init; }
    public string? Position { get; init; }
    public bool IsCommercial { get; init; }
    public required string BasePath { get; init; }

    // Pack list for switcher
    public IReadOnlyList<CompliancePackSummary> AvailablePacks { get; init; } = [];

    // Guardian status
    public int GuardianCount { get; init; }
    public int GuardianAlerts { get; init; }
    public IReadOnlyList<GuardianLogEntry> RecentGuardianRuns { get; init; } = [];

    // Override stats
    public int TotalOverrides { get; init; }
    public int FalsePositives { get; init; }
    public int FalseNegatives { get; init; }

    // DSAR queue
    public int PendingDsarRequests { get; init; }
    public int OverdueDsarRequests { get; init; }

    // Drift
    public IReadOnlyList<DriftItem> DriftAlerts { get; init; } = [];
}

public sealed record CompliancePackSummary(string Id, string Name, string? Jurisdiction);
public sealed record GuardianLogEntry(string Name, string Status, DateTime ExecutedAt, int RecordsAffected);
public sealed record DriftItem(string Setting, string Expected, string Actual, string Message);
