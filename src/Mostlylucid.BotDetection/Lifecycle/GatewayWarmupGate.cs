using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Cold-start safety net for behavioural bot detectors. Combines a
///     gateway-wide warmup check (process uptime + total observed requests)
///     with a per-signature observation-count floor. Behavioural detectors
///     query this gate before contributing a bot signal so freshly-booted
///     gateways and brand-new signatures don't pollute the model with
///     under-sampled inference.
/// </summary>
/// <remarks>
///     <para>
///         Sibling of <see cref="UpstreamHealthGate"/>: same shape, different
///         cold-start dimension. <c>UpstreamHealthGate</c> protects
///         status-derived bot signals when the protected site is cold-starting
///         or down (upstream cold). <c>GatewayWarmupGate</c> protects
///         BEHAVIOURAL bot signals when stylobot itself just booted and
///         classifiers haven't accumulated enough samples to be reliable
///         (gateway cold). Both gates compose by OR: a detector that gates
///         on either should skip when EITHER says "stand down", never
///         introducing a third independent skip path.
///     </para>
///     <para>
///         Rules still fire (operator-authored). Identity / UA / header /
///         honeypot detection still runs (those score from the first
///         observation). Only multi-request-derived behavioural arms gate
///         on this signal.
///     </para>
///     <para>
///         Per <c>feedback_centroid_learning_feedback_loop</c>, the
///         orchestrator entry stamps every detection event with
///         <c>signals[gateway.warmup]</c> so persisted centroid samples can
///         be segmented out of the natural prior. The stamp uses the
///         gateway-wide dimension only at orchestrator entry; detectors
///         that know their per-signature observation count call
///         <see cref="IsWarmedUp(long)"/> with that count for the
///         finer-grained per-signature decision.
///     </para>
///     <para>
///         Reuses <see cref="DegradationAtom.TotalSamples"/> as the
///         gateway-wide request counter -- no new counter needed. The atom
///         already increments per response in the upstream-health pipeline,
///         and the count is monotonic since process start, which is exactly
///         what the warmup gate needs.
///     </para>
///     <para>
///         Pure read; no state besides the captured start time. Safe to
///         register as singleton.
///     </para>
/// </remarks>
public sealed class GatewayWarmupGate
{
    private readonly DegradationAtom _atom;
    private readonly IOptions<GatewayWarmupOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;

    public GatewayWarmupGate(
        DegradationAtom atom,
        IOptions<GatewayWarmupOptions> options,
        TimeProvider? timeProvider = null)
    {
        _atom = atom ?? throw new ArgumentNullException(nameof(atom));
        _options = options ?? Microsoft.Extensions.Options.Options.Create(new GatewayWarmupOptions());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    ///     Gateway-wide warmup check. Returns <c>true</c> when both
    ///     uptime and total-request thresholds are crossed and behavioural
    ///     detectors should score normally; returns <c>false</c> during
    ///     the cold-start window. When
    ///     <see cref="GatewayWarmupOptions.EnableWarmupGate"/> is false
    ///     this always returns <c>true</c>.
    /// </summary>
    /// <remarks>
    ///     Equivalent to <c>IsWarmedUp(long.MaxValue)</c> -- the per-signature
    ///     observation floor is bypassed.
    /// </remarks>
    public bool IsWarmedUp() => IsWarmedUp(long.MaxValue);

    /// <summary>
    ///     Two-dimensional warmup check. Returns <c>true</c> when BOTH
    ///     (a) the gateway-wide warmup window has closed and (b) the
    ///     supplied signature has crossed
    ///     <see cref="GatewayWarmupOptions.MinSignatureSamples"/> observations.
    ///     Returns <c>false</c> when either dimension is still cold.
    /// </summary>
    /// <param name="signatureObservationCount">
    ///     Total observations recorded for the per-request signature
    ///     (typically pulled from the signature aggregate or session-request
    ///     count signal). Pass <c>long.MaxValue</c> to bypass the
    ///     per-signature dimension and ask only "is the gateway warm?".
    /// </param>
    public bool IsWarmedUp(long signatureObservationCount)
    {
        var opts = _options.Value;
        if (!opts.EnableWarmupGate)
            return true;

        // Gateway-wide dimension: both uptime + sample-count floors.
        var elapsed = _timeProvider.GetUtcNow() - _startedAt;
        if (elapsed < opts.WarmupDuration)
            return false;
        if (_atom.TotalSamples < opts.MinGatewaySamples)
            return false;

        // Per-signature dimension: even after the gateway is warm, a new
        // signature isn't behaviourally stable until it crosses the floor.
        // long.MaxValue / very high values skip this branch.
        if (signatureObservationCount < opts.MinSignatureSamples)
            return false;

        return true;
    }
}