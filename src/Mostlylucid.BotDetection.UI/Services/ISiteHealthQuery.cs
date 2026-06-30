using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Dashboard-side read of the gateway's site-health history ring. One
///     method, one window token (15m / 1h / 24h / 7d), returns the bounded
///     slice oldest-first. Per <c>project_gateway_data_locality</c> the
///     ring lives on the gateway -- the dashboard never caches; the
///     implementation is a thin REST client, the view component early-returns
///     gracefully when this service isn't registered
///     (<c>feedback_remote_mode_optional_di</c>).
/// </summary>
public interface ISiteHealthQuery
{
    /// <summary>
    ///     Fetch the gateway's bounded <c>DegradationHistoryAtom</c> ring
    ///     sliced to the requested <paramref name="window"/>. Implementations
    ///     return an empty list on transport failure so the view component
    ///     can render the empty-state branch without bubbling exceptions.
    /// </summary>
    Task<IReadOnlyList<DegradationSnapshot>> GetHistoryAsync(string window, CancellationToken ct = default);
}
