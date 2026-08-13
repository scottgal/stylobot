using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.TagHelpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     D2 (P0 2026-08-13, cold-first-load stuck widgets): the dashboard shell AND the
///     page view both emit &lt;sb-live-updates /&gt; (e.g. Traffic/Index.cshtml + the
///     dashboard shell Index.cshtml), so an unguarded page emitted the live-updates
///     script stack twice — every instance opened its OWN SignalR hub connection (4
///     connections observed per page load) and duplicated id="sb-live-toggle" broke the
///     pause toggle. The helper now emits once per request; later invocations render
///     nothing.
/// </summary>
public sealed class SbLiveUpdatesTagHelperDedupeTests
{
    private static (string rendered, TagHelperOutput output) Render(
        SbLiveUpdatesTagHelper helper, TagHelperContext context)
    {
        var output = new TagHelperOutput(
            "sb-live-updates",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        helper.Process(context, output);
        var rendered = output.PreContent.GetContent()
            + output.Content.GetContent()
            + output.PostContent.GetContent();
        return (rendered, output);
    }

    private static (SbLiveUpdatesTagHelper helper, TagHelperContext context) Build()
    {
        var http = new DefaultHttpContext();
        var helper = new SbLiveUpdatesTagHelper(
            new HttpContextAccessor { HttpContext = http },
            new StyloBotDashboardOptions());
        var context = new TagHelperContext(
            tagName: "sb-live-updates",
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");
        return (helper, context);
    }

    [Fact]
    public void Second_invocation_in_the_same_request_emits_nothing()
    {
        var (helper, context) = Build();

        var (first, _) = Render(helper, context);
        var (second, _) = Render(helper, context);

        Assert.Contains("sb-live-updates.js", first);
        Assert.Contains("signalr.min.js", first);
        Assert.Equal(string.Empty, second);
    }

    [Fact]
    public void Fresh_request_emits_the_script_stack_again()
    {
        var (helper, context) = Build();

        var (first, _) = Render(helper, context);

        // A new request (new HttpContext) emits again — the dedupe is per-request, not global.
        var http2 = new DefaultHttpContext();
        var helper2 = new SbLiveUpdatesTagHelper(
            new HttpContextAccessor { HttpContext = http2 },
            new StyloBotDashboardOptions());
        var context2 = new TagHelperContext(
            tagName: "sb-live-updates",
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test2");
        var (second, _) = Render(helper2, context2);

        Assert.Contains("sb-live-updates.js", first);
        Assert.Contains("sb-live-updates.js", second);
    }
}
