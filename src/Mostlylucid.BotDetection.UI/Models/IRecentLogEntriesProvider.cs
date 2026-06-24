using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Read-side adapter for surfacing recent log entries on the log-sink dashboard
///     view. Implementations point at WHATEVER cache the logs already flow through —
///     they do NOT introduce a parallel ring buffer or shadow store.
///     <para>
///         The intended commercial implementation reads from
///         <c>Stylobot.Commercial.OtelMesh.Correlation.FingerprintTimelineAtom</c>,
///         the per-fingerprint LFU sliding cache that already indexes every span /
///         log / metric the gateway receives. Surfacing its content here just adds
///         a read path; the cache itself is unchanged.
///     </para>
///     <para>
///         FOSS hosts where commercial OtelMesh isn't registered get a null
///         registration and the view renders the "no log lines yet" empty state.
///     </para>
/// </summary>
public interface IRecentLogEntriesProvider
{
    /// <summary>
    ///     Snapshot the top <paramref name="limit"/> recent log entries in the cache,
    ///     ordered by the cache's own LFU priority (fingerprint hit-count) then by
    ///     recency within a fingerprint. Bodies MUST be PII-scrubbed before return —
    ///     this is the operator-facing surface and the per-host privacy contract
    ///     must not regress.
    /// </summary>
    IReadOnlyList<RecentLogEntry> GetRecent(int limit);
}

/// <summary>
///     Display DTO for a single log entry surfaced on the log-sink view. The
///     <see cref="FingerprintPriority"/> field carries the LFU hit-count for the
///     entry's fingerprint so the view can render "where this fits in the cache"
///     instead of asking the operator to take the order on faith.
/// </summary>
public sealed record RecentLogEntry(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    string ScrubbedBody,
    string? FingerprintId,
    long FingerprintPriority);