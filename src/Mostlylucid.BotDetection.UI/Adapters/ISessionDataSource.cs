using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.UI.Adapters;

/// <summary>
///     Pluggable session data source for the dashboard's signature detail API
///     (<c>/_stylobot/api/sessions/signature/{sig}</c>).
///
///     The dashboard middleware checks for this service first. If registered, it
///     delegates entirely to the implementation. If not, it falls back to the default
///     behaviour: reading from the local <c>ISessionStore</c>.
///
///     This enables two deployment topologies without code changes:
///     <list type="bullet">
///       <item>
///         <b>DB topology (commercial default)</b>: both gateway and dashboard share a
///         PostgreSQL instance. <c>PostgreSQLSessionStore</c> is registered as
///         <c>ISessionStore</c> -- no <c>ISessionDataSource</c> needed at all.
///         The gateway writes sessions; the dashboard reads them directly.
///       </item>
///       <item>
///         <b>API topology</b>: dashboard has no direct DB access. Register
///         <c>GatewayApiSessionDataSource</c> pointing at the gateway's base URL.
///         The dashboard proxies session requests to the gateway and returns the
///         pre-computed JSON (including radar axes).
///       </item>
///     </list>
///
///     Implementations must write a valid JSON array to <c>context.Response.Body</c>
///     and set <c>context.Response.ContentType = "application/json"</c>.
/// </summary>
public interface ISessionDataSource
{
    Task ServeAsync(HttpContext context, string signature, CancellationToken ct = default);
}
