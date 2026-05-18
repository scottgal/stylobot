namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Single entry point for the detection pipeline + dashboards. Aggregates
///     <see cref="IThreatIntelProvider"/>s, filters past-expiry verdicts, and
///     queues background enrichment for live providers on cache miss.
/// </summary>
public interface IThreatIntelCoordinator
{
    /// <summary>
    ///     True when threat-intel is globally enabled AND at least one provider
    ///     is registered. The contributor short-circuits early when this is false
    ///     so we don't pay the per-request overhead.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    ///     Hot-path lookup across every enabled provider. Returns non-expired
    ///     verdicts; allocation-light (empty array when nothing matches).
    /// </summary>
    IReadOnlyList<ThreatIntelVerdict> Lookup(ThreatSubject subject);

    /// <summary>
    ///     Background-only. Live providers fetch + cache <paramref name="subject"/>;
    ///     offline providers no-op (their refresh is feed-level, not subject-level).
    ///     Safe to fire-and-forget. Subsequent <see cref="Lookup"/> calls return the
    ///     fetched verdict.
    /// </summary>
    Task EnrichAsync(ThreatSubject subject, CancellationToken cancellationToken);

    /// <summary>Registered providers, ordered as configured. For diagnostics / dashboard.</summary>
    IReadOnlyList<IThreatIntelProvider> Providers { get; }
}
