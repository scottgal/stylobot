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
    private readonly IOptionsMonitor<UpstreamHealthOptions> _options;

    public UpstreamHealthGate(DegradationAtom atom, IOptions<UpstreamHealthOptions> options)
    {
        _atom = atom ?? throw new ArgumentNullException(nameof(atom));
        _options = new StaticMonitor(options?.Value ?? new UpstreamHealthOptions());
    }

    /// <summary>
    ///     Convenience overload that allows hot-reload via
    ///     <see cref="IOptionsMonitor{T}"/>. The DI container always selects
    ///     the <see cref="IOptions{T}"/> overload to avoid ambiguous-ctor
    ///     resolution; consumers wanting reload can construct directly.
    /// </summary>
    public static UpstreamHealthGate WithMonitor(
        DegradationAtom atom,
        IOptionsMonitor<UpstreamHealthOptions> options)
    {
        ArgumentNullException.ThrowIfNull(atom);
        ArgumentNullException.ThrowIfNull(options);
        return new UpstreamHealthGate(atom, options);
    }

    private UpstreamHealthGate(DegradationAtom atom, IOptionsMonitor<UpstreamHealthOptions> options)
    {
        _atom = atom;
        _options = options;
    }

    /// <summary>
    ///     Returns <c>true</c> when upstream looks healthy and
    ///     status-derived bot signals should fire normally; returns
    ///     <c>false</c> only when both (a) we have enough samples to
    ///     decide and (b) either the 5xx or 4xx EMA crosses its
    ///     configured threshold.
    /// </summary>
    public bool IsUpstreamHealthy()
    {
        var opts = _options.CurrentValue;
        if (_atom.TotalSamples < opts.MinSampleCount)
            return true;

        var rate5xx = _atom.GetSignalValue(DegradationAtom.Error5xxRate);
        if (rate5xx > opts.Unhealthy5xxThreshold)
            return false;

        var rate4xx = _atom.GetSignalValue(DegradationAtom.Error4xxRate);
        if (rate4xx > opts.Unhealthy4xxThreshold)
            return false;

        return true;
    }

    private sealed class StaticMonitor : IOptionsMonitor<UpstreamHealthOptions>
    {
        public StaticMonitor(UpstreamHealthOptions value) { CurrentValue = value; }
        public UpstreamHealthOptions CurrentValue { get; }
        public UpstreamHealthOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<UpstreamHealthOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }
}