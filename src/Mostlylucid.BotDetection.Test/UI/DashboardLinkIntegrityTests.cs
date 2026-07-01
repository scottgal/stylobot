using System.Text.RegularExpressions;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Guards two dashboard-link invariants that a security product must never ship broken:
///
///     1. NO HARDCODED MOUNT POINT. The dashboard is mounted at a configurable base path
///        (e.g. <c>/_stylobot</c> when embedded, <c>/dashboard</c> on the public site). Views
///        MUST build links from the base path (<c>@basePath</c> / <c>@bp</c> / <c>@Model.BasePath</c>,
///        sourced from <c>ViewData["BasePath"]</c> or the page model) — never a literal
///        <c>href="/dashboard/…"</c> or <c>href="/_stylobot/…"</c>. A hardcoded prefix silently
///        breaks every link under any non-default mount.
///
///     2. NO LINK TO AN UNIMPLEMENTED ROUTE. A link to a dashboard section the router does not
///        dispatch renders the "Unknown dashboard section" fallback (HTTP 200, dead body).
///
///     Live incident this pins (2026-07-01): Top-visitors / Threats cards hardcoded
///     <c>/dashboard/visitors/{sig}</c> and Top-endpoints hardcoded <c>/dashboard/site/endpoint</c>
///     — wrong mount prefix AND unimplemented sections. Correct: <c>@basePath/signature/{sig}</c>
///     and <c>@basePath/endpoints?selected=METHOD|PATH</c>.
/// </summary>
public class DashboardLinkIntegrityTests
{
    // First path segment after the base path that the middleware router dispatches to a real
    // body (StyloBotDashboardMiddleware section switch). Keep in sync with that switch. Only the
    // LINKABLE detail/list sections referenced via <a href> — API/partial routes are JS-fetched.
    private static readonly HashSet<string> LinkableSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "traffic", "overview", "insights", "investigate", "activity",
        "visitors", "site", "signature", "endpoint", "endpoints", "session", "entity",
        "signals", "help", "policies", "configuration", "compliance",
        "aspnet-pack", "otel-mesh",
        "api" // api/* routes (config download, exports, …) are legitimate <a href> targets

    };

    // Hardcoded absolute mount-point literals that must never appear in a dashboard view href.
    private static readonly Regex HardcodedMount =
        new(@"href=""(/dashboard/|/_stylobot/)", RegexOptions.IgnoreCase);

    // A base-path-relative href: href="@bp/section...", href="@basePath/section...",
    // href="@Model.BasePath/section...", href="@navBp/section...". Captures the section segment.
    private static readonly Regex BasePathHref =
        new(@"href=""@(?:bp|basePath|navBp|Model\.BasePath|Model\.ResolvedNavBasePath)/([a-z0-9_-]+)",
            RegexOptions.IgnoreCase);

    [Fact]
    public void DashboardViewsUseBasePathAndOnlyLinkToImplementedRoutes()
    {
        var viewsDir = LocateDashboardViewsDir();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(viewsDir, "*.cshtml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            var text = File.ReadAllText(file);

            // (1) No hardcoded mount point.
            foreach (System.Text.RegularExpressions.Match m in HardcodedMount.Matches(text))
                offenders.Add($"{name}: HARDCODED mount '{m.Groups[1].Value}…' — use @basePath / @bp / @Model.BasePath");

            // (2) Base-path-relative links must target an implemented section.
            foreach (System.Text.RegularExpressions.Match m in BasePathHref.Matches(text))
            {
                var section = m.Groups[1].Value;
                if (!LinkableSections.Contains(section))
                    offenders.Add($"{name}: link to unimplemented dashboard section '{section}' (would render 'Unknown dashboard section')");
            }
        }

        Assert.True(offenders.Count == 0,
            "Dashboard view link integrity violations:\n  " + string.Join("\n  ", offenders.Distinct()));
    }

    private static string LocateDashboardViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Mostlylucid.BotDetection.UI", "Views", "StyloBot", "Dashboard");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard from " + AppContext.BaseDirectory);
    }
}