namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Action taken when a <see cref="PolicyRule"/> matches. Concrete shapes
///     carry whatever metadata the enforcement layer needs (challenge kind,
///     tag name, rate-limit budget, etc).
/// </summary>
public abstract record PolicyAction
{
    /// <summary>Let the request through untouched.</summary>
    public sealed record Allow : PolicyAction;

    /// <summary>Record that the rule matched but otherwise let the request through. Used in <see cref="PolicyMode.Observe"/>-paired authoring.</summary>
    public sealed record Observe : PolicyAction;

    /// <summary>Attach <paramref name="Name"/> as a labelled tag to the request for downstream consumers.</summary>
    public sealed record Tag(string Name) : PolicyAction;

    /// <summary>
    ///     Present a challenge of the specified <paramref name="Kind"/>
    ///     (e.g. <c>"turnstile"</c>, <c>"captcha"</c>, <c>"jschallenge"</c>).
    /// </summary>
    public sealed record Challenge(string Kind) : PolicyAction;

    /// <summary>Apply a rate limit of <paramref name="RequestsPerMinute"/> requests per minute on the matched cohort.</summary>
    public sealed record RateLimit(int RequestsPerMinute) : PolicyAction;

    /// <summary>Block the request outright.</summary>
    public sealed record Block : PolicyAction;
}
