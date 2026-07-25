using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
/// Regression coverage for the batch/HTMX Top Bots path (<see cref="SbWidgetBatchMiddleware"/>'s
/// <c>BuildBatchWindow</c>), which produced the staging "scoped domain count &gt; all-domains" bug.
/// The shared page bundle was composed with a null/targeted audience — and <c>GetTopBotsAsync</c>
/// maps a null audience to BOTS-ONLY — so the UNSCOPED Top Bots header read Humans=0/Internal=0,
/// while a scoped all-audience self-fetch out-counted it (scoped &gt; unscoped, impossible for a
/// subset). The composed window must be ALL-AUDIENCE (so unscoped shows the true breakdown and a
/// domain-scoped render is a genuine subset) and must carry the selected domain(s).
/// </summary>
public sealed class SbTopBotsBatchPathDomainScopeTests
{
    private static HttpContext ContextWithQuery(string queryString)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString(queryString);
        return ctx;
    }

    [Fact]
    public void BuildBatchWindow_composes_all_audience_even_when_audience_query_is_bots()
    {
        var window = SbWidgetBatchMiddleware.BuildBatchWindow(ContextWithQuery("?audience=bots"));

        // All-audience is load-bearing: a null/bots audience made the composed BotAggregate
        // bots-only, zeroing the header's Humans/Internal and letting a scoped all-audience
        // view out-count the unscoped one.
        Assert.Equal("all", window.AudienceFilter);
    }

    [Fact]
    public void BuildBatchWindow_composes_all_audience_when_audience_absent()
    {
        var window = SbWidgetBatchMiddleware.BuildBatchWindow(ContextWithQuery("?window=24h"));

        Assert.Equal("all", window.AudienceFilter);
    }

    [Fact]
    public void BuildBatchWindow_threads_selected_domain_into_window()
    {
        var window = SbWidgetBatchMiddleware.BuildBatchWindow(ContextWithQuery("?domain=example.com"));

        Assert.NotNull(window.Domains);
        Assert.Contains("example.com", window.Domains!);
    }

    [Fact]
    public void BuildBatchWindow_with_no_domain_leaves_domains_null()
    {
        var window = SbWidgetBatchMiddleware.BuildBatchWindow(ContextWithQuery("?window=24h"));

        Assert.Null(window.Domains);
    }
}
