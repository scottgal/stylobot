namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Per-upstream snapshot store written by the active probe tick and read
///     by <see cref="UpstreamHealthGate"/> during cold-start or idle windows
///     when <see cref="DegradationAtom"/> has not yet accumulated enough
///     passive samples to decide alone.
/// </summary>
public interface IActiveUpstreamProbeState
{
    /// <summary>
    ///     Records (or replaces) the latest probe result for the named upstream.
    /// </summary>
    void Update(string upstreamKey, ActiveProbeSnapshot snapshot);

    /// <summary>
    ///     Returns the most recent <see cref="ActiveProbeSnapshot"/> for the
    ///     named upstream, or <c>null</c> if no probe has run yet.
    ///     Used by the dashboard dual-row display (healthy + probe columns).
    /// </summary>
    ActiveProbeSnapshot? Latest(string upstreamKey);

    /// <summary>
    ///     Worst-case fold across all known upstreams: any upstream whose
    ///     <see cref="ActiveProbeSnapshot.Status"/> is <c>"unhealthy"</c>
    ///     causes the whole fold to return <c>false</c>; at least one known
    ///     upstream with no unhealthy entry returns <c>true</c>; no known
    ///     upstreams returns <c>null</c>.
    ///     <c>"unknown"</c> snapshots count as known-but-not-unhealthy
    ///     and do not force the fold to false.
    /// </summary>
    /// <remarks>
    ///     The worst-case fold is deliberate: <see cref="UpstreamHealthGate.IsUpstreamHealthy"/>
    ///     gates whether status-derived detectors (<see cref="Orchestration.Atoms.ClaimedIdentityAtom"/>,
    ///     <see cref="Detectors.HeuristicDetector"/>, the response-status boost,
    ///     404 scan-pattern contributor, reputation lane error indicators,
    ///     heuristic 404 weights, and the Markov NotFound transition) trust
    ///     response status codes as bot evidence. Any upstream degradation makes
    ///     those signals unreliable, so pessimism is the safe default: one
    ///     known-unhealthy upstream suppresses the whole status-derived
    ///     contribution rather than letting a partially-degraded cluster
    ///     contaminate reputation scores.
    /// </remarks>
    bool? AggregateHealthy();
}
