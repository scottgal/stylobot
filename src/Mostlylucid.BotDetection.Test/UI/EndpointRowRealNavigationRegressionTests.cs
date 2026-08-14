using System.IO;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Si2 (endpoint-IA unification), task 3: the endpoint row in SbEndpointsList used to
///     hx-get a fragment into a sibling #endpoint-detail-panel div instead of navigating to
///     a real page -- because no real endpoint-detail page existed. Now that
///     {basePath}/endpoint/{method}/{path} renders inside the full dashboard chrome
///     (EndpointDetailChromeIntegrationTests), the row click is a genuine &lt;a href&gt;,
///     mirroring _TopEndpointsCard.cshtml's already-correct pattern (a real navigation, not
///     an htmx inline-swap) exactly.
///
///     Source-level pins because a TestServer round trip for "does this <tr> contain an
///     &lt;a href&gt; instead of an hx-get" is a disproportionate scaffold for a markup
///     shape assertion -- same rationale DashboardSignatureRouteRegressionTests.cs gives.
/// </summary>
public class EndpointRowRealNavigationRegressionTests
{
    private const string DefaultCshtmlRelativePath =
        "src/Mostlylucid.BotDetection.UI/Views/Shared/Components/SbEndpointsList/Default.cshtml";

    private const string TopEndpointsCardRelativePath =
        "src/Mostlylucid.BotDetection.UI/Views/StyloBot/Dashboard/Traffic/_TopEndpointsCard.cshtml";

    [Fact]
    public void Endpoint_row_navigates_via_a_href_not_an_htmx_inline_swap()
    {
        var src = ReadRepoFile(DefaultCshtmlRelativePath);

        Assert.Contains("<a href=\"@methodHref\"", src);
        Assert.Contains("$\"{bp}/endpoint/", src);
        // Operator P0 (2026-08-14): the empty-path rows must never render the method-as-id
        // dead link — the href builder returns null and the cells render text-only.
        Assert.Contains("string? EndpointHref(string method, string path)", src);
        Assert.Contains("if (string.IsNullOrWhiteSpace(path)) return null;", src);
        Assert.DoesNotContain("hx-target=\"#endpoint-detail-panel\"", src);
        Assert.DoesNotContain("partials/endpoint-detail", src);
    }

    [Fact]
    public void Compact_embeds_carry_a_view_all_link_to_the_canonical_site_route()
    {
        // Si2 task 4 (consolidation): the Traffic overview's "Top content pages"
        // widget and the Activity sidebar list both embed this same Default.cshtml
        // via <sb-endpoints-list compact="true">, not a separate implementation.
        // A compact glance should point back at the canonical, fully-filterable
        // list -- source-grepped for the same "disproportionate TestServer scaffold"
        // reason as the other markup-shape assertions in this file.
        var src = ReadRepoFile(DefaultCshtmlRelativePath);

        Assert.Contains("Model.IsCompact", src);
        Assert.Contains("View all", src);
        Assert.Contains("href=\"@bp/site\"", src);
    }

    [Fact]
    public void TopEndpointsCard_links_to_the_new_endpoint_detail_route()
    {
        var src = ReadRepoFile(TopEndpointsCardRelativePath);

        Assert.Contains("/endpoint/", src);
        // The old target resolved to nothing in the RenderPage=true (FOSS default)
        // topology: no matching sub-row was registered in FossDashboardGroups, so
        // DashboardRowRegistry.Resolve returned null and Index.cshtml rendered
        // "Unknown dashboard section." Assembled from two literals so this guard
        // itself doesn't trip on its own explanatory comment in the source file.
        Assert.DoesNotContain("/site" + "/endpoint", src);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}
