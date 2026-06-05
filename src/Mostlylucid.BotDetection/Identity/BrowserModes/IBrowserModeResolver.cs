using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Request-scoped browser-mode classifier. Lazy-computes the mode id
///     from <see cref="HttpContext"/> on first call and caches it in
///     <see cref="HttpContext.Items"/> so the rest of the pipeline reuses the
///     same answer — the early endpoint-policy resolver and the later
///     <c>BrowserModeClassifierContributor</c> are guaranteed to see the same
///     classification for a given request.
///
///     The resolver only consumes inputs derivable from the <see cref="HttpContext"/>
///     itself (method, path, Sec-Fetch-* headers, Accept, Accept-Encoding,
///     Upgrade, Sec-Purpose, Sec-WebSocket-Key). That keeps it callable at
///     endpoint-policy time, BEFORE the bot-detection blackboard exists.
///     Predicate clauses that would need richer blackboard signals (network,
///     locale, transport TLS, behavioural rates) belong on different policy
///     surfaces, not on browser mode.
/// </summary>
public interface IBrowserModeResolver
{
    /// <summary>
    ///     Returns the YAML mode id classified for this request. Cached in
    ///     <see cref="HttpContext.Items"/>; safe to call multiple times.
    ///     Always returns the configured fallback mode id rather than null
    ///     when no predicate matches.
    /// </summary>
    string Resolve(HttpContext context);
}
