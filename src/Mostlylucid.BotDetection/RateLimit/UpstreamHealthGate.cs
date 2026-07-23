using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Read-only verdict over <see cref="DegradationAtom"/>: is upstream
///     healthy enough that response status codes should still be treated
///     as bot-behaviour evidence?
/// </summary>
/// <remarks>
///     <para>
///         When upstream is cold-starting or down, the gateway returns
///         4xx/5xx via YARP. Without this gate, status-derived detectors
///         (the response-status boost, 404 scan-pattern contributor,
///         reputation lane error indicators, heuristic 404 weights, and
///         the Markov NotFound transition) attribute those status codes
///         to bot probing and falsely elevate legitimate visitors during
///         the outage window.
///     </para>
///     <para>
///         The five sites query this gate before applying their
///         status-derived contribution. When the gate says "unhealthy",
///         they short-circuit the bot-signal contribution but keep
///         signals like honeypot hits and 429s intact, since those are
///         meaningful regardless of upstream health.
///     </para>
///     <para>
///         Per
///         <c>feedback_centroid_learning_feedback_loop</c>, every
///         persisted detection event also carries an
///         <c>upstream.healthy</c> signal so post-hoc centroid analyses
///         can segment outage windows out of the natural prior.
///     </para>
///     <para>
///         Pure read; no state. Safe to register as singleton.
///     </para>
/// </remarks>
public sealed class UpstreamHealthGate
{
    private readonly DegradationAtom _atom;
    private readonly IOptions<UpstreamHealthOptions> _options;
    private readonly IActiveUpstreamProbeState? _probeState;

    public UpstreamHealthGate(
        DegradationAtom atom,
        IOptions<UpstreamHealthOptions> options,
        IActiveUpstreamProbeState? probeState = null)
    {
        _atom = atom ?? throw new ArgumentNullException(nameof(atom));
        _options = options ?? Microsoft.Extensions.Options.Options.Create(new UpstreamHealthOptions());
        _probeState = probeState;
    }

    /// <summary>
    ///     Returns <c>true</c> when upstream looks healthy and
    ///     status-derived bot signals should fire normally; returns
    ///     <c>false</c> only when upstream is confirmed unhealthy by
    ///     passive EWMA (once enough samples exist) or by the active probe
    ///     (during cold-start or idle windows).
    /// </summary>
    /// <remarks>
    ///     Composite logic: passive <see cref="DegradationAtom"/> data is
    ///     authoritative when it has accumulated at least
    ///     <see cref="UpstreamHealthOptions.MinSampleCount"/> samples.
    ///     When passive data is sufficient, the 5xx/4xx EMA thresholds decide
    ///     and active probe results are intentionally ignored -- a confirmed
    ///     outage must not be diluted by a probe that happened to succeed
    ///     moments before the cascade. When passive data is insufficient
    ///     (cold-start or idle gap), <see cref="IActiveUpstreamProbeState.AggregateHealthy"/>
    ///     fills the gap: one known-unhealthy upstream makes the whole gate
    ///     report unhealthy. This pessimism is correct because the gate
    ///     controls whether <see cref="Orchestration.Atoms.ClaimedIdentityAtom"/>,
    ///     <see cref="Detectors.HeuristicDetector"/>, the response-status
    ///     boost, 404 scan-pattern contributor, reputation lane error
    ///     indicators, heuristic 404 weights, and the Markov NotFound
    ///     transition treat response status codes as bot evidence. Any
    ///     upstream degradation makes those signals unreliable, so
    ///     pessimism prevents false bot attributions during outage windows.
    ///     If no active state is injected (<c>null</c>), the gate preserves
    ///     the original unconditional cold-start-true behaviour.
    /// </remarks>
    public bool IsUpstreamHealthy()
    {
        var opts = _options.Value;
        if (_atom.TotalSamples >= opts.MinSampleCount)
        {
            // Passive has enough samples: passive decides (unchanged existing verdict).
            var rate5xx = _atom.GetSignalValue(DegradationAtom.Error5xxRate);
            if (rate5xx > opts.Unhealthy5xxThreshold)
                return false;

            var rate4xx = _atom.GetSignalValue(DegradationAtom.Error4xxRate);
            if (rate4xx > opts.Unhealthy4xxThreshold)
                return false;

            return true;
        }

        // Passive data-starved (cold-start / idle): active fills the gap.
        return _probeState?.AggregateHealthy() ?? true;
    }
}