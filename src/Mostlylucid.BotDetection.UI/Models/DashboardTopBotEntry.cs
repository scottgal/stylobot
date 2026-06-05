namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top bot entry from the event store, used by the /api/topbots endpoint.
/// </summary>
public sealed record DashboardTopBotEntry
{
    public required string PrimarySignature { get; init; }

    /// <summary>
    ///     Durable entity id currently bound to this row's primary signature, or
    ///     null when no active edge exists. Lets the dashboard URL the row at
    ///     <c>/dashboard/entity/{id}</c> instead of the rotation-fragile
    ///     <c>/dashboard/signature/{PrimarySignature}</c>. Populated server-side
    ///     by the dashboard event store via a correlated subquery against
    ///     <c>entity_edges</c>; absent entries fall back to the signature URL in
    ///     the view.
    /// </summary>
    public string? EntityId { get; init; }

    public int HitCount { get; init; }
    public string? BotName { get; init; }
    public string? CustomBotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public double BotProbability { get; init; }
    public double Confidence { get; init; }
    public string? Action { get; init; }
    public string? CountryCode { get; init; }
    public double ProcessingTimeMs { get; init; }
    public List<string>? TopReasons { get; init; }
    public string? LastPath { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public string? Narrative { get; init; }
    public string? Description { get; init; }
    public bool IsKnownBot { get; init; }
    public double? ThreatScore { get; init; }
    public string? ThreatBand { get; init; }

    /// <summary>
    ///     True when <c>VerifiedBotContributor</c> confirmed the UA's bot claim
    ///     against an operator IP range or via FCrDNS round-trip. Drives the
    ///     green tick on the row's verification badge. The "spoofed" state is
    ///     still derived from the <c>Spoofed-</c> name prefix that contributor
    ///     uses when the IP fails verification.
    /// </summary>
    public bool IsVerifiedBot { get; init; }

    /// <summary>Total bytes sent in responses attributed to detections for this signature.</summary>
    public long BytesOut { get; init; }

    /// <summary>
    ///     Resolved UA family name (Chrome, Firefox, curl, etc.) from the
    ///     UaProfileStore. Used as a fallback identity label when neither
    ///     <see cref="BotName"/> nor a real <see cref="BotType"/> resolved --
    ///     a real signal we already extract during detection, not a
    ///     hash-derived invention.
    /// </summary>
    public string? UaFamily { get; init; }

    /// <summary>
    ///     Raw <c>User-Agent</c> string of the most-recent detection row in
    ///     the aggregation window. Used by <see cref="Services.SignatureDisplayName.Resolve"/>
    ///     to surface "Chrome 119 / macOS" -style client labels via uap-core
    ///     parsing -- the dashboard's primary "show what we know" signal for
    ///     identifying ordinary visitors. Null when the persisted detection
    ///     captured no UA (rare; happens on internal stub callers).
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    ///     60-bucket per-minute hit count, oldest-first (index 0 = 59 minutes ago,
    ///     index 59 = current minute). Empty array when the ring buffer has no
    ///     observations yet. Used by the _Sparkline primitive for SSR svg paths.
    /// </summary>
    public int[] HitTrend { get; init; } = Array.Empty<int>();

    /// <summary>
    ///     SignatureRiskVerdictComposer's RiskProfileLabel as a string -- one of
    ///     <c>Hostile</c> / <c>VerifiedCommunity</c> / <c>Aligned</c> /
    ///     <c>Drifting</c> / <c>Unknown</c>. Overlayed onto the row from
    ///     /api/v1/topbots when a friendly-pin or hostile-pin fires. Null on
    ///     rows the composer has no opinion about.
    /// </summary>
    public string? RiskProfile { get; init; }

    /// <summary>
    ///     One-line justification for the composed verdict, written by the
    ///     SignatureRiskVerdictComposer. Operator-facing reason like
    ///     <c>"Verified-friendly (ip+domain): identity corroborated by non-UA signal"</c>
    ///     or <c>"Confirmed bad: reputation abort or honeypot hit on this signature"</c>.
    /// </summary>
    public string? Justification { get; init; }

    /// <summary>
    ///     Structured reasons emitted by the composer (e.g.
    ///     <c>"hostile_pin: confirmed_bad latch true"</c>,
    ///     <c>"friendly_pin: cluster_type=Safe"</c>). The dashboard renders these
    ///     as a small "Why" block under the row.
    /// </summary>
    public IReadOnlyList<string>? VerdictReasons { get; init; }

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - LastSeen;
            if (span.TotalSeconds < 5) return "now";
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)span.TotalDays}d";
        }
    }
}
