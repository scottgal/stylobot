using System.Net;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Drives the running Demo app through real HTTP and asserts that the dashboard
///     routing introduced in the left-nav refactor (Tasks 2-10) plus the M2
///     dashboard-IA collapse work end-to-end:
///     <list type="bullet">
///         <item>Each surviving FOSS left-nav row (Traffic, Visitors, Site,
///         Policies, Configuration) returns 200 with the dashboard chrome.</item>
///         <item>Legacy <c>?tab=X</c> query-string URLs 301 to <c>/stylobot/X</c>
///         (for X that still exists post-M2: traffic, visitors, configuration,
///         endpoints — the latter then chains a second 301 to /site).</item>
///         <item>Additional query params survive the 301.</item>
///         <item>Bare <c>/stylobot/</c> 301-redirects to <c>/stylobot/traffic</c>
///         when V2Enabled (M1's landing redirect).</item>
///         <item>Each deleted legacy URL (overview, activity, sessions, threats,
///         insights, investigate, endpoints) 301s to its V2 collapse target.</item>
///         <item>Unknown area renders the "Unknown dashboard section" panel at 200.</item>
///         <item>Legacy <c>/stylobot/countries</c> still dispatches for deep links.</item>
///     </list>
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
public sealed class DashboardLeftNavRoutingTests
{
    private readonly DemoAppFactory _app;

    public DashboardLeftNavRoutingTests(DemoAppFactory app)
    {
        _app = app;
    }

    /// <summary>
    ///     A BROWSER-SHAPED client. These tests previously used a bare
    ///     <c>new HttpClient()</c> and asserted on its treatment, which tests the wrong thing:
    ///     the dashboard sits BEHIND detection (<c>UseStyloBot()</c> registers
    ///     <c>UseBotDetection()</c> before <c>StyloBotDashboardMiddleware</c>, which owns the
    ///     legacy 301s), so a headerless client scores tool-shaped and is enforced with 403
    ///     before the redirect ever runs. Every one of these tests failed on 403-instead-of-301.
    ///
    ///     <para>
    ///         MEASURED 2026-08-08 against a live Demo app on <c>/stylobot/overview</c>:
    ///         bare curl got 403, while <b>every</b> realistic browser shape got the expected
    ///         301 — modern Chrome, Firefox 131 and Safari 17.6 (neither sends <c>sec-ch-ua</c>;
    ///         it is Chromium-only), Chrome with <c>Sec-Fetch-*</c> stripped by a privacy
    ///         extension, and Chrome behind a corporate proxy sending <c>Via</c>. So the 403 is
    ///         confined to genuinely tool-shaped clients, and these tests should present as
    ///         what a real user is.
    ///     </para>
    ///
    ///     <para>
    ///         This is deliberately Firefox-shaped: it carries no <c>sec-ch-ua</c>, so the
    ///         fixture cannot silently start depending on Chromium client hints being present.
    ///     </para>
    /// </summary>
    private HttpClient BrowserClient(bool followRedirects = true)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = followRedirects };
        var client = new HttpClient(handler) { BaseAddress = new Uri(_app.BaseUrl) };
        var h = client.DefaultRequestHeaders;
        h.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14.5; rv:131.0) Gecko/20100101 Firefox/131.0");
        h.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        h.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.5");
        h.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
        h.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        h.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        h.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        return client;
    }

    [Theory]
    [InlineData("/stylobot/traffic")]
    [InlineData("/stylobot/visitors")]
    [InlineData("/stylobot/site")]
    [InlineData("/stylobot/policies")]
    [InlineData("/stylobot/configuration")]
    public async Task Foss_row_returns_200_with_left_nav(string path)
    {
        using var client = BrowserClient();
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        // V2 sidebar (M1) uses sb-left-nav-v2 as its outer id; legacy fall-back
        // keeps sb-left-nav. Accept either so the test reflects what M1/M2
        // actually shipped without coupling to which one ran for this request.
        Assert.True(
            html.Contains("id=\"sb-left-nav\"") || html.Contains("id=\"sb-left-nav-v2\""),
            "Expected one of sb-left-nav / sb-left-nav-v2 in rendered dashboard HTML.");
    }

    [Theory]
    [InlineData("/stylobot/?tab=traffic",       "/stylobot/traffic")]
    [InlineData("/stylobot/?tab=visitors",      "/stylobot/visitors")]
    [InlineData("/stylobot/?tab=configuration", "/stylobot/configuration")]
    public async Task Old_tab_querystring_301s_to_new_route(string from, string expectedTo)
    {
        using var client = BrowserClient(followRedirects: false);
        var res = await client.GetAsync(from);
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal(expectedTo, res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Old_tab_querystring_preserves_other_query_params()
    {
        using var client = BrowserClient(followRedirects: false);
        var res = await client.GetAsync("/stylobot/?tab=traffic&fp=abc");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("/stylobot/traffic?fp=abc", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Bare_dashboard_redirects_to_traffic_landing()
    {
        // M1 flipped V2Enabled on by default; M2 keeps the bare /dashboard
        // landing redirect targeted at /traffic so the operator's first paint
        // is the new aggregate. The redirect is a 302 (temporary) because the
        // landing target may flip again as packs add capabilities; the
        // *content* moves are still 301s (M2 below).
        using var client = BrowserClient(followRedirects: false);
        var res = await client.GetAsync("/stylobot/");
        Assert.True(
            res.StatusCode == HttpStatusCode.MovedPermanently ||
            res.StatusCode == HttpStatusCode.Found,
            $"Expected 301 or 302 from bare /stylobot/; got {(int)res.StatusCode}.");
        Assert.Equal("/stylobot/traffic", res.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/stylobot/overview",    "/stylobot/traffic")]
    [InlineData("/stylobot/activity",    "/stylobot/traffic")]
    [InlineData("/stylobot/sessions",    "/stylobot/visitors")]
    [InlineData("/stylobot/insights",    "/stylobot/traffic")]
    [InlineData("/stylobot/investigate", "/stylobot/visitors")]
    [InlineData("/stylobot/endpoints",   "/stylobot/site")]
    public async Task Legacy_IA_url_301s_to_V2_target(string from, string expectedTo)
    {
        // M2 collapses the legacy 10-tab dashboard into Traffic / Visitors /
        // Site. The seven deleted URLs (overview, activity, sessions, threats,
        // insights, investigate, endpoints) each 301 to their new home so
        // bookmarks, Slack pastes, and telemetry traces survive the rip.
        using var client = BrowserClient(followRedirects: false);
        var res = await client.GetAsync(from);
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal(expectedTo, res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Legacy_threats_url_301s_to_visitors_with_threat_filter()
    {
        // /threats is special-cased to land on Visitors pre-filtered to the
        // medium-and-up threat band — the V2 IA collapses the standalone
        // threats list into a Visitors filter pill (plan spec §9).
        using var client = BrowserClient(followRedirects: false);
        var res = await client.GetAsync("/stylobot/threats");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        // %2B is "+" URL-encoded; the redirect target spells out the threat
        // band query the V2 Visitors page consumes.
        Assert.Equal("/stylobot/visitors?threat=Medium%2B", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Unknown_area_renders_unknown_section_panel()
    {
        using var client = BrowserClient();
        var res = await client.GetAsync("/stylobot/does-not-exist");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("Unknown dashboard section", html);
    }

    [Fact]
    public async Task Legacy_countries_route_renders_for_back_compat()
    {
        using var client = BrowserClient();
        var res = await client.GetAsync("/stylobot/countries");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}