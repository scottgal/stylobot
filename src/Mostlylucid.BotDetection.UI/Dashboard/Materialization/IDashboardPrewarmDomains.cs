namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     The managed-domain set the pinned prewarm must cover (operator 2026-08-16: the
///     law that every pinned window warms in the background covers EVERY managed domain,
///     not just the all-domains keys). The dashboard's domain-scoped envelopes
///     (manifest, window, domains=[X]) are distinct content-cache keys — before this
///     seam the pinned pass warmed only the domains=null keys, so a domain-pill
///     selection read a forever-cold envelope (summary 0/0/0 + "No data in this
///     window" while the store had the rows). Null/empty on hosts without a domain
///     concept (FOSS-only) — the prewarm keeps the all-domains behaviour.
/// </summary>
public interface IDashboardPrewarmDomains
{
    /// <summary>The managed domain names (e.g. <c>stylo.bot</c>, <c>aspnet-staging.stylobot.net</c>)
    ///     whose (manifest, window) envelopes the pinned pass must keep warm.</summary>
    IReadOnlyList<string> GetManagedDomains();
}
