namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Behaviour when bot detection itself fails (orchestrator exception, store unavailable,
///     sidecar unreachable, etc.). This is NOT the verdict for a bot-positive request:
///     bots are still blocked according to action policies. FailureMode covers the case
///     where the pipeline could not produce a verdict at all.
/// </summary>
public enum FailureMode
{
    /// <summary>
    ///     Allow the request through on internal failure. Bias toward availability.
    ///     Default for general-purpose sites and the sidecar pattern (where the sidecar
    ///     being unreachable should not take down the upstream app).
    /// </summary>
    FailOpen = 0,

    /// <summary>
    ///     Reject the request with HTTP 503 (Service Unavailable) on internal failure.
    ///     Bias toward security. Use for high-security endpoints where letting a
    ///     potentially-bot request through unscanned is worse than dropping a legitimate
    ///     one (admin panels, financial transactions, account changes).
    /// </summary>
    FailClosed = 1,

    /// <summary>
    ///     Allow the request through, but write a diagnostic signal to the response
    ///     headers and structured logs so operators can monitor the failure rate without
    ///     impacting users. Useful for staged rollouts and shadow-mode evaluation of
    ///     FailClosed before flipping it on.
    /// </summary>
    LogOnly = 2,
}
