namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Configuration for <see cref="GatewayWarmupGate"/>. Binds to
///     <c>BotDetection:GatewayWarmup</c> in the host configuration.
/// </summary>
/// <remarks>
///     <para>
///         Two warmup dimensions are gated together:
///     </para>
///     <list type="number">
///         <item>
///             <b>Gateway-wide:</b> until <c>(now - startTime) &gt;= WarmupDuration</c>
///             AND total observed requests >= <see cref="MinGatewaySamples"/>,
///             behavioural detectors stand down across the board because the
///             gateway hasn't yet accumulated a representative shape sample.
///         </item>
///         <item>
///             <b>Per-signature:</b> even after the gateway is warm, a NEW
///             signature with fewer than <see cref="MinSignatureSamples"/>
///             observations is not behaviourally stable yet. Behavioural
///             inference stands down FOR THAT SIGNATURE only; other
///             signatures with enough history score normally.
///         </item>
///     </list>
///     <para>
///         Rules still fire (operator-authored, no statistical floor required).
///         Identity / UA / header / honeypot detection still runs (those work
///         from the first observation). Only BEHAVIOURAL (multi-request-derived)
///         contributors are gated.
///     </para>
/// </remarks>
public sealed class GatewayWarmupOptions
{
    /// <summary>
    ///     Minimum process-uptime before the gateway-wide warmup gate is
    ///     allowed to flip to "warmed-up". Default 3 minutes: enough time for
    ///     the first burst of cold-start requests to surface representative
    ///     identity / UA distribution without leaving the behavioural
    ///     classifiers blind for hours.
    /// </summary>
    public TimeSpan WarmupDuration { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     Minimum total observed requests before the gateway-wide warmup
    ///     gate is allowed to flip to "warmed-up". Default 200: representative
    ///     enough to seed centroids without committing the behavioural arms
    ///     until prior shape exists. Composes AND with
    ///     <see cref="WarmupDuration"/> so both must be satisfied -- a
    ///     gateway that booted 10 minutes ago but has seen 3 requests is
    ///     still in warmup.
    /// </summary>
    public int MinGatewaySamples { get; set; } = 200;

    /// <summary>
    ///     Minimum per-signature observations before behavioural inference
    ///     fires for that signature. Default 8: a brand-new signature with
    ///     two requests cannot be reliably classified by Markov / session
    ///     vector / drift detectors; their contributions stand down until
    ///     this floor is crossed. Other signatures with more history are
    ///     unaffected.
    /// </summary>
    public int MinSignatureSamples { get; set; } = 8;

    /// <summary>
    ///     Master switch. <c>true</c> (default) keeps the warmup safety net
    ///     active. Setting <c>false</c> short-circuits
    ///     <see cref="GatewayWarmupGate.IsWarmedUp(long)"/> to always return
    ///     <c>true</c> -- useful for tests that need detectors to score from
    ///     the first request, but unsafe in production because behavioural
    ///     centroids will bake cold-start shape.
    /// </summary>
    public bool EnableWarmupGate { get; set; } = true;
}