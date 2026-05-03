namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR hub contract for real-time bot detection dashboard updates.
///     Uses a beacon-only pattern: the server sends lightweight invalidation signals
///     and clients fetch updated data via HTMX partial endpoints.
///     No data payloads are sent over SignalR except for attack arc animations.
/// </summary>
public interface IStyloBotDashboardHub
{
    /// <summary>
    ///     Lightweight invalidation signal. Tells connected clients that a specific
    ///     data category has changed without sending the full data payload.
    ///     The HTMX coordinator uses this to trigger OOB partial refreshes.
    /// </summary>
    /// <param name="signal">
    ///     The invalidation signal name, matching widget <c>data-sb-depends</c> values
    ///     (e.g., "summary", "signature", "countries", "clusters", "useragents"),
    ///     or a specific signature hash for per-signature updates.
    /// </param>
    Task BroadcastInvalidation(string signal);

    /// <summary>
    ///     Lightweight attack arc signal for the world map visualization.
    ///     Carries only the minimum data needed to render an arc animation.
    ///     This is the ONLY method that carries a data payload.
    /// </summary>
    Task BroadcastAttackArc(string countryCode, string riskBand);
}
