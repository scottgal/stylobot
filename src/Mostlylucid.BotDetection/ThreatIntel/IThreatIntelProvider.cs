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

    /// <summary>
    ///     Diagnostic snapshot for dashboards. Allocation-light, no locks held by
    ///     the caller, no I/O. Live providers populate quota + breaker fields;
    ///     offline providers leave them at their defaults.
    /// </summary>
    ProviderStatus GetStatus();
}

/// <summary>
///     Unified per-provider snapshot for the dashboard threat-intel tab. Both
///     offline and live providers produce this shape; vendor-specific fields
///     (quota, breaker) are optional and live providers fill them in.
/// </summary>
public sealed record ProviderStatus
{
    public required string Provider { get; init; }
    public required ThreatIntelMode Mode { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>Number of entries in the in-memory cache after the most recent refresh.</summary>
    public int CacheSize { get; init; }

    /// <summary>UTC timestamp of the last successful refresh, or null if never refreshed.</summary>
    public DateTime? LastRefreshUtc { get; init; }

    /// <summary>How often the refresh service plans to call <see cref="IThreatIntelProvider.RefreshAsync"/>.</summary>
    public TimeSpan RefreshInterval { get; init; }

    /// <summary>True when the most recent refresh failed (cache still serves the previous result).</summary>
    public bool LastRefreshFailed { get; init; }

    /// <summary>Live providers: per-UTC-day call counter.</summary>
    public int QuotaUsed { get; init; }

    /// <summary>Live providers: per-UTC-day cap. 0 = quota disabled / not applicable.</summary>
    public int DailyQuota { get; init; }

    /// <summary>Live providers: UTC date the quota counter applies to.</summary>
    public DateTime? QuotaDateUtc { get; init; }

    /// <summary>Live providers: when the circuit breaker opens until (past = closed).</summary>
    public DateTime? BreakerOpenUntilUtc { get; init; }

    /// <summary>Live providers: errors in the last 1-minute window.</summary>
    public int ErrorsInWindow { get; init; }
}
