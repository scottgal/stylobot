namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     A single threat-intel data source. Implementations either pull a feed into
///     an in-memory cache (offline) or hit a vendor API per subject (live). The
///     hot path only ever calls <see cref="TryLookup"/>; <see cref="RefreshAsync"/>
///     is invoked from <see cref="ThreatIntelRefreshService"/> on a staggered
///     schedule. See <c>docs/architecture/threat-intel.md</c>.
/// </summary>
public interface IThreatIntelProvider
{
    /// <summary>Unique provider identifier used in config keys + signal names.</summary>
    string Name { get; }

    /// <summary>Offline (cache-only) vs live (per-subject HTTP).</summary>
    ThreatIntelMode Mode { get; }

    /// <summary>Which subject types this provider knows how to answer.</summary>
    IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; }

    /// <summary>
    ///     Hot-path-safe lookup. MUST NOT block on I/O. Returns the cached verdict
    ///     for <paramref name="subject"/>, or null if not cached / not supported.
    ///     Past-expiry verdicts are returned (the coordinator filters them) so
    ///     providers don't have to repeat the expiry check.
    /// </summary>
    ThreatIntelVerdict? TryLookup(ThreatSubject subject);

    /// <summary>
    ///     Background work. Offline providers ignore <paramref name="subject"/> and
    ///     refresh the whole feed. Live providers fetch the supplied subject if
    ///     non-null. Re-entrant; coordinator may call concurrently from the refresh
    ///     service AND from the per-fingerprint background-enrichment hook.
    /// </summary>
    Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken);

    /// <summary>
    ///     Approximate refresh interval. The coordinator uses this to detect
    ///     stale-cache situations and to compute the per-provider stagger offset.
    /// </summary>
    TimeSpan RefreshInterval { get; }
}
