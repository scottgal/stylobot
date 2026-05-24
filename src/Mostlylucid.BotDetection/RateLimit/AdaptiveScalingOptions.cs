namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Configuration block driving the adaptive bot-rate-limit multiplier
///     (phase 4 of the policy-grammar work).
/// </summary>
/// <remarks>
///     <para>
///         When <see cref="Enabled"/> is true and the upstream's P95
///         latency / 5xx rate cross a configured tier threshold, every
///         <see cref="Actions.RateLimitActionPolicy"/> scales its effective
///         <c>RequestsPerMinute</c> by the tier's <see cref="Tier.BotMultiplier"/>.
///         Humans never traverse rate limits, so this is what operationalises
///         the "prioritise humans" guarantee from the plan.
///     </para>
///     <para>
///         The default tier ladder is conservative: bot allowance halves at
///         P95 &gt; 1000ms (or 3% 5xx) and drops to 10% at P95 &gt; 2000ms
///         (or 10% 5xx). Override per deployment in
///         <c>appsettings.json</c> via <c>BotDetection:RateLimit:AdaptiveScaling</c>.
///     </para>
/// </remarks>
public sealed class AdaptiveScalingOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Tier ladder, evaluated top-down. The *first* tier whose
    ///     threshold is exceeded wins; tiers below that one are ignored.
    ///     A signal that doesn't exceed any threshold yields the
    ///     1.0 baseline.
    /// </summary>
    public List<Tier> Tiers { get; set; } =
    [
        new() { Name = "nominal",  P95LatencyMs =  500, Error5xxRatePct =  1, BotMultiplier = 1.0 },
        new() { Name = "degraded", P95LatencyMs = 1000, Error5xxRatePct =  3, BotMultiplier = 0.5 },
        new() { Name = "critical", P95LatencyMs = 2000, Error5xxRatePct = 10, BotMultiplier = 0.1 },
    ];

    public HysteresisOptions Hysteresis { get; set; } = new();

    public sealed class Tier
    {
        /// <summary>Human-readable name (rendered on the dashboard chip).</summary>
        public string Name { get; set; } = "";

        /// <summary>P95 latency threshold in ms. <c>0</c> = ignore latency for this tier.</summary>
        public double P95LatencyMs { get; set; }

        /// <summary>5xx error-rate threshold in percent (0-100). <c>0</c> = ignore for this tier.</summary>
        public double Error5xxRatePct { get; set; }

        /// <summary>
        ///     Multiplier applied to <see cref="Actions.RateLimitActionOptions.RequestsPerMinute"/>
        ///     while this tier is active. 1.0 = nominal, 0.5 = halve bot
        ///     allowance, 0.1 = 10% of nominal.
        /// </summary>
        public double BotMultiplier { get; set; } = 1.0;
    }

    public sealed class HysteresisOptions
    {
        /// <summary>
        ///     How long a tier's condition must continuously hold before the
        ///     tier is considered "entered". Stops a one-request 5xx burst
        ///     from halving the bot allowance.
        /// </summary>
        public double DwellSeconds { get; set; } = 30;

        /// <summary>
        ///     When health recovers, multiply the *current* multiplier by
        ///     this each time we step back a tier. Coming back up is slower
        ///     than going down -- prevents oscillation when a service
        ///     wobbles right around a threshold.
        /// </summary>
        public double RecoveryMultiplier { get; set; } = 0.8;
    }
}
