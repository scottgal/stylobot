namespace Mostlylucid.BotDetection.Policies.Dispatch;

/// <summary>
///     Outcome of <see cref="PolicyActionDispatcher.DispatchAsync"/>. Tells
///     <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware"/>
///     whether the HTTP response is already shaped (short-circuit) or whether
///     the legacy <c>DetectionPolicyAction</c> path should continue running.
/// </summary>
public enum PolicyDispatchResult
{
    /// <summary>
    ///     The dispatcher already shaped the HTTP response. The middleware
    ///     must short-circuit and return without running the legacy enum
    ///     path or invoking <c>_next</c>.
    /// </summary>
    Handled,

    /// <summary>
    ///     The dispatcher did NOT shape the response. The middleware should
    ///     continue to the legacy <c>DetectionPolicyAction</c> switch (and
    ///     ultimately to the next middleware) unchanged.
    /// </summary>
    FallThrough
}
