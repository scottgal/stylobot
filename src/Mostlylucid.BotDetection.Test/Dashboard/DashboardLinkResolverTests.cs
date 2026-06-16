using FluentAssertions;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Coverage for <see cref="DashboardLinkResolver" />, the single
///     chokepoint that turns a dashboard-relative subpath into a
///     host-absolute URL prefixed by the configured BasePath / NavBasePath.
///     <para>
///         Existed because the FOSS UI hard-coded <c>/dashboard/...</c>
///         into health-summary providers, page builders, and several
///         dashboard partials. Anywhere the dashboard middleware was
///         mounted at a non-default <c>BasePath</c> -- the ASP.NET
///         Trailblazor demo at <c>/_stylobot</c>, an operator who picked
///         a custom path -- those hard-coded links 404'd. The resolver
///         is the option-driven fix the providers + cshtml partials now
///         hit; these tests pin the surface so it can't silently rot.
///     </para>
/// </summary>
public sealed class DashboardLinkResolverTests
{
    // ---------- 1. Default options -> /stylobot mount. ----------

    [Fact]
    public void Resolve_uses_default_BasePath_when_NavBasePath_is_null()
    {
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions());

        resolver.NavBasePath.Should().Be("/stylobot");
        resolver.Resolve("/policies").Should().Be("/stylobot/policies");
    }

    // ---------- 2. Custom BasePath (Trailblazor demo) flows through. ----------

    [Fact]
    public void Resolve_follows_configured_BasePath_when_NavBasePath_unset()
    {
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot"
        });

        resolver.NavBasePath.Should().Be("/_stylobot");
        resolver.Resolve("/aspnet-hub").Should().Be("/_stylobot/aspnet-hub");
    }

    // ---------- 3. NavBasePath overrides BasePath (commercial embedded chrome). ----------

    [Fact]
    public void Resolve_prefers_NavBasePath_over_BasePath_when_set()
    {
        // Mirrors the commercial deployment where the dashboard middleware
        // serves API + partials at /stylobot but operator-facing nav links
        // point at the host's own /Dashboard chrome.
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/stylobot",
            NavBasePath = "/Dashboard"
        });

        resolver.NavBasePath.Should().Be("/Dashboard");
        resolver.Resolve("/insights").Should().Be("/Dashboard/insights");
    }

    // ---------- 4. Empty subpath returns the bare base path. ----------

    [Fact]
    public void Resolve_returns_bare_base_path_for_empty_subpath()
    {
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot"
        });

        resolver.Resolve(string.Empty).Should().Be("/_stylobot");
    }

    // ---------- 5. Leading slash on subpath is normalised, not duplicated. ----------

    [Fact]
    public void Resolve_normalises_leading_slash_on_subpath()
    {
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot"
        });

        // Both produce one slash between base path and subpath. Callers
        // shouldn't have to think about this; the resolver owns it.
        resolver.Resolve("policies").Should().Be("/_stylobot/policies");
        resolver.Resolve("/policies").Should().Be("/_stylobot/policies");
    }

    // ---------- 6. Trailing slash on BasePath is trimmed. ----------

    [Fact]
    public void Resolve_trims_trailing_slash_on_BasePath()
    {
        // Operators writing the appsettings literal sometimes type the
        // trailing slash; the resolver must produce a single separator
        // either way.
        var resolver = new DashboardLinkResolver(new StyloBotDashboardOptions
        {
            BasePath = "/_stylobot/"
        });

        resolver.Resolve("/policies").Should().Be("/_stylobot/policies");
    }
}
