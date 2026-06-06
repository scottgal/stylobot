using System.Net;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Drives the running Demo app through real HTTP and asserts that the left-nav
///     dashboard routing introduced in Tasks 2-10 works end-to-end:
///     <list type="bullet">
///         <item>Every FOSS left-nav row returns 200 and renders <c>#sb-left-nav</c>.</item>
///         <item>Legacy <c>?tab=X</c> query-string URLs 301 to <c>/stylobot/X</c>.</item>
///         <item>Additional query params are preserved through the 301.</item>
///         <item>Bare <c>/stylobot/</c> renders Overview (SSR seed element present).</item>
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

    [Theory]
    [InlineData("/stylobot/overview")]
    [InlineData("/stylobot/activity")]
    [InlineData("/stylobot/visitors")]
    [InlineData("/stylobot/endpoints")]
    [InlineData("/stylobot/sessions")]
    [InlineData("/stylobot/threats")]
    [InlineData("/stylobot/policies")]
    [InlineData("/stylobot/configuration")]
    public async Task Foss_row_returns_200_with_left_nav(string path)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("id=\"sb-left-nav\"", html);
    }

    [Theory]
    [InlineData("/stylobot/?tab=overview",      "/stylobot/overview")]
    [InlineData("/stylobot/?tab=activity",      "/stylobot/activity")]
    [InlineData("/stylobot/?tab=endpoints",     "/stylobot/endpoints")]
    [InlineData("/stylobot/?tab=configuration", "/stylobot/configuration")]
    public async Task Old_tab_querystring_301s_to_new_route(string from, string expectedTo)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync(from);
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal(expectedTo, res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Old_tab_querystring_preserves_other_query_params()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync("/stylobot/?tab=overview&fp=abc");
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.Equal("/stylobot/overview?fp=abc", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Bare_dashboard_renders_overview_by_default()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync("/stylobot/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("id=\"time-chart-seed\"", html);
    }

    [Fact]
    public async Task Unknown_area_renders_unknown_section_panel()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync("/stylobot/does-not-exist");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("Unknown dashboard section", html);
    }

    [Fact]
    public async Task Legacy_countries_route_renders_for_back_compat()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var res = await client.GetAsync("/stylobot/countries");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}