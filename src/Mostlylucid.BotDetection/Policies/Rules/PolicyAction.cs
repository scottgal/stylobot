namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Action taken when a <see cref="PolicyRule"/> matches. Concrete shapes
///     carry whatever metadata the enforcement layer needs (challenge kind,
///     tag name, rate-limit budget, etc).
/// </summary>
public abstract record PolicyAction
{
    public abstract PolicyIntentKind Intent { get; }

    /// <summary>Let the request through untouched.</summary>
    public sealed record Allow : PolicyAction
    {
        public override PolicyIntentKind Intent => PolicyIntentKind.Allow;
    }

    /// <summary>Record that the rule matched but otherwise let the request through. Used in <see cref="PolicyMode.Observe"/>-paired authoring.</summary>
    public sealed record Observe : PolicyAction
    {
        public override PolicyIntentKind Intent => PolicyIntentKind.Observe;
    }

    /// <summary>Attach <paramref name="Name"/> as a labelled tag to the request for downstream consumers.</summary>
    public sealed record Tag(string Name) : PolicyAction
    {
        public override PolicyIntentKind Intent => PolicyIntentKind.Tag;
    }

    /// <summary>
    ///     Present a challenge of the specified <paramref name="Kind"/>
    ///     (e.g. <c>"turnstile"</c>, <c>"captcha"</c>, <c>"jschallenge"</c>).
    /// </summary>
    public sealed record Challenge(string Kind) : PolicyAction
    {
        public override PolicyIntentKind Intent => PolicyIntentKind.Challenge;
    }

    /// <summary>Apply a rate limit of <paramref name="RequestsPerMinute"/> requests per minute on the matched cohort.</summary>
    public sealed record RateLimit(int RequestsPerMinute) : PolicyAction
    {
        public override PolicyIntentKind Intent => PolicyIntentKind.Throttle;
    }

    /// <summary>
    ///     Process-wide rate cap keyed by the rule's <see cref="PolicyScope"/>.
    ///     Unlike <see cref="RateLimit"/> (per-key, default fingerprint),
    ///     Throttle's bucket is shared across every request that matches the
    ///     rule's scope -- so a rule with scope <c>{ host: domain(x.com) }</c>
    ///     + <c>Throttle(50)</c> caps total traffic on that domain at 50 req/s
    ///     while armed.
    ///
    ///     <para>
    ///         Pairs with the trigger machine: a meter-only predicate
    ///         transitions the rule to ARMED when the meter sustains over
    ///         threshold, the cap engages globally for the rule's scope, then
    ///         lifts when the meter recovers.
    ///     </para>
    ///
    ///     <para>
    ///         <see cref="RequestsPerSecond"/> must be &gt; 0; the constructor
    ///         enforces this so callers don't accidentally arm a zero-cap that
    ///         silently blocks every request.
    ///     </para>
    /// </summary>
    public sealed record Throttle : PolicyAction
    {
        public Throttle(int requestsPerSecond, string? reason = null)
        {
            if (requestsPerSecond <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(requestsPerSecond),
                    requestsPerSecond,
                    "Throttle.RequestsPerSecond must be > 0.");
            RequestsPerSecond = requestsPerSecond;
            Reason = reason;
        }

        /// <summary>Steady-state cap in requests/sec. Always &gt; 0.</summary>
        public int RequestsPerSecond { get; init; }

        /// <summary>Optional operator-facing label surfaced in decision-log entries.</summary>
        public string? Reason { get; init; }

        public override PolicyIntentKind Intent => PolicyIntentKind.Throttle;
    }

    /// <summary>Block the request outright.</summary>
    public sealed record Block : PolicyAction
    {
        /// <summary>
        ///     HTTP status to return. Null (the default) keeps the historical 403 Forbidden.
        ///     Set to 404 for a "this path does not exist" deflection -- e.g. a raw <c>.env</c> /
        ///     config-file scan, where a 403 confirms the path is real and worth retrying, but a
        ///     404 tells the scanner nothing exists (and never leaks even if the upstream would 200).
        /// </summary>
        public int? Status { get; init; }

        public override PolicyIntentKind Intent => PolicyIntentKind.Block;
    }
}
