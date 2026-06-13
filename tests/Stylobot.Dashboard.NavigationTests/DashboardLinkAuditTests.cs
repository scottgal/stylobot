using FluentAssertions;
using Microsoft.Playwright;
using Stylobot.Dashboard.NavigationTests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace Stylobot.Dashboard.NavigationTests;

/// <summary>
///     Live navigation audit against staging.stylobot.net (override with
///     <c>STYLOBOT_NAV_BASE_URL</c>). Catches the class of regression that
///     shipped in 34e2d747: route rewrites that point a working link at
///     a page that doesn't consume the new query param, leaving deep links
///     stranded on a bare list.
///
///     Each test is dispositive on URL-level evidence -- the page URL after
///     a click is the user-visible contract; DOM markers (such as
///     <c>[data-testid='sb-signature-detail']</c>) corroborate. Failure modes
///     are reported with the route that misbehaved so the bisect is short.
/// </summary>
public class DashboardLinkAuditTests : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _pw;
    private readonly ITestOutputHelper _output;

    public DashboardLinkAuditTests(PlaywrightFixture pw, ITestOutputHelper output)
    {
        _pw = pw;
        _output = output;
    }

    /// <summary>
    ///     Pull a real, currently-rendered signature id from the dashboard so
    ///     the direct-GET test stays valid across deploys. The endpoints page
    ///     always renders at least one row that links to a signature -- even
    ///     a fresh staging host gets traffic from the bot scanners that find
    ///     the public hostname within minutes. Falls back to the visitors
    ///     page if endpoints has none yet.
    /// </summary>
    private async Task<string> DiscoverSignatureIdAsync(IPage page)
    {
        foreach (var surface in new[] { "/dashboard/endpoints", "/dashboard/visitors", "/dashboard" })
        {
            await page.GotoAsync(
                $"{TargetEnvironment.BaseUrl}{surface}",
                new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 60000 });

            try
            {
                await page.Locator("a[href*='/dashboard/signature/']").First
                    .WaitForAsync(new() { Timeout = 10000 });
            }
            catch { /* surface has no signature link, try the next one */ }

            var raw = await page.EvaluateAsync<string?>(@"() => {
                for (const el of document.querySelectorAll('[href], [data-href]')) {
                    const v = el.getAttribute('href') || el.getAttribute('data-href') || '';
                    const m = v.match(/\/dashboard\/signature\/([^?#""\s]+)/);
                    if (m) return decodeURIComponent(m[1]);
                }
                return null;
            }");
            if (!string.IsNullOrEmpty(raw)) return raw;
        }

        throw new InvalidOperationException(
            $"No signature link discoverable on any seed surface of {TargetEnvironment.BaseUrl}; " +
            "the navigation suite can't run without at least one live signature.");
    }

    private async Task<IBrowserContext> NewContextAsync()
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(TargetEnvironment.BypassKey))
            headers["X-StyloBot-Bypass"] = TargetEnvironment.BypassKey;
        return await _pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = headers
        });
    }

    /// <summary>
    ///     Regression for 34e2d747. A direct GET on a known signature URL
    ///     must NOT 302 to <c>/dashboard/endpoints?selectedSig=</c>. The
    ///     final page URL is the contract -- if the redirect comes back, the
    ///     URL will carry the orphan query param.
    /// </summary>
    [Fact]
    public async Task Direct_GET_signature_detail_does_not_redirect_to_endpoints()
    {
        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        var sigId = await DiscoverSignatureIdAsync(page);
        _output.WriteLine($"discovered signature id: {sigId}");

        var response = await page.GotoAsync(
            $"{TargetEnvironment.BaseUrl}/dashboard/signature/{Uri.EscapeDataString(sigId)}",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 60000 });

        response.Should().NotBeNull();
        page.Url.Should().Contain("/dashboard/signature/", "the signature route must serve the detail page directly");
        page.Url.Should().NotContain("selectedSig", "no orphan query param redirect is permitted");
        page.Url.Should().NotContain("/dashboard/endpoints", "signature links must NEVER land on the endpoints page");

        var marker = page.Locator("[data-testid='sb-signature-detail']");
        await marker.WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
        var markerSig = await marker.GetAttributeAsync("data-signature-id");
        markerSig.Should().NotBeNullOrEmpty("the page must render the signature detail panel anchored to a sig id");
    }

    /// <summary>
    ///     Walks the endpoints page, finds the first signature link in the
    ///     rendered DOM, follows it, and asserts the landing URL is a real
    ///     signature detail. Catches the case where a future presenter
    ///     re-introduces a non-signature destination for a signature link.
    /// </summary>
    [Fact]
    public async Task Clicking_signature_link_on_endpoints_page_lands_on_signature_detail()
    {
        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync(
            $"{TargetEnvironment.BaseUrl}/dashboard/endpoints",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 60000 });

        var allSigLinks = page.Locator("[data-href*='/signature/'], a[href*='/dashboard/signature/']");
        await allSigLinks.First.WaitForAsync(new() { Timeout = 30000 });
        var count = await allSigLinks.CountAsync();
        count.Should().BeGreaterThan(0, "the endpoints page should expose at least one signature link in the bot table");
        var sigLink = allSigLinks.First;

        var rawHref = await sigLink.GetAttributeAsync("href") ?? await sigLink.GetAttributeAsync("data-href");
        rawHref.Should().NotBeNullOrEmpty();
        _output.WriteLine($"following first signature link: {rawHref}");

        await sigLink.ScrollIntoViewIfNeededAsync();
        // NoWaitAfter skips Playwright's internal "wait for navigation done"; the
        // dashboard pages hold the load event open with long-poll SignalR and
        // would otherwise time out before we can assert on the URL.
        await sigLink.ClickAsync(new() { NoWaitAfter = true });
        await page.WaitForURLAsync(
            u => u.Contains("/dashboard/signature/") || u.Contains("selectedSig"),
            new() { Timeout = 30000, WaitUntil = WaitUntilState.Commit });

        page.Url.Should().Contain("/dashboard/signature/");
        page.Url.Should().NotContain("selectedSig");
        page.Url.Should().NotMatch(".*/dashboard/endpoints($|\\?)");

        await page.Locator("[data-testid='sb-signature-detail']")
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 10000 });
    }

    /// <summary>
    ///     Scans every common dashboard surface for href / data-href
    ///     attributes that contain <c>selectedSig</c>. If any survive, a
    ///     view template has been rewired against the route contract. This
    ///     pairs with the source-pin in <c>DashboardSignatureRouteRegressionTests</c>:
    ///     the source-pin catches the middleware; this test catches the views.
    /// </summary>
    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/dashboard/endpoints")]
    [InlineData("/dashboard/sessions")]
    [InlineData("/dashboard/threats")]
    [InlineData("/dashboard/visitors")]
    [InlineData("/dashboard/policies")]
    public async Task No_dashboard_page_emits_a_selectedSig_link(string path)
    {
        await using var context = await NewContextAsync();
        var page = await context.NewPageAsync();

        var resp = await page.GotoAsync(
            $"{TargetEnvironment.BaseUrl}{path}",
            new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 60000 });
        resp.Should().NotBeNull();

        // Some dashboard surfaces (e.g. the index when not public) auth-gate.
        // Skip the audit cleanly rather than treating an auth wall as a
        // navigation regression -- the audit is about emitted links, not auth.
        if ((int)resp!.Status == 401 || (int)resp.Status == 403)
        {
            _output.WriteLine($"skipping {path}: auth-gated (status {resp.Status})");
            return;
        }
        ((int)resp.Status).Should().BeLessThan(400, $"{path} must render for the audit to be meaningful");

        // Anchors are server-rendered, so we don't need full DOMContentLoaded.
        // Give the body element a chance to materialise then enumerate hrefs.
        await page.Locator("body").WaitForAsync(new() { Timeout = 30000 });

        var badLinks = await page.EvaluateAsync<string[]>(@"() => {
            const out = [];
            for (const el of document.querySelectorAll('[href], [data-href]')) {
                const href = el.getAttribute('href') || '';
                const dataHref = el.getAttribute('data-href') || '';
                if (href.includes('selectedSig') || dataHref.includes('selectedSig')) {
                    out.push(href || dataHref);
                }
            }
            return out;
        }");

        badLinks.Should().BeEmpty(
            $"{path} emitted link(s) targeting the broken endpoints?selectedSig redirect contract");
    }
}