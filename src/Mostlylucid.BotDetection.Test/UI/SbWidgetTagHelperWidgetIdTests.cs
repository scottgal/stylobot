using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Gated re-activation (2026-09-09) of the client-update machinery for the Traffic
///     page: the <c>&lt;sb-widget&gt;</c> shell must be able to declare a widget IDENTITY.
///     <para>
///         Contract this pins: <c>sb-live-updates.js</c> builds its depends→widget map and
///         targets its OOB swaps by enumerating <c>[data-sb-widget]</c>, and
///         <c>SbWidgetBatchMiddleware.RenderWidgetAsync</c> dispatches on that same id. The
///         shell emitted <c>class</c> + <c>data-sb-depends</c> but never
///         <c>data-sb-widget</c>, so every tag-helper widget — including the hits-per-period
///         chart, whose <c>depends="summary"</c> was therefore dead — was invisible to the
///         beacon and could never be refreshed. That is the operator's "the traffic graph
///         never updates" complaint.
///     </para>
/// </summary>
public sealed class SbWidgetTagHelperWidgetIdTests
{
    private static TagHelperOutput Render(SbWidgetTagHelper helper)
    {
        var context = new TagHelperContext(
            tagName: "sb-widget",
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");
        var output = new TagHelperOutput(
            "sb-widget",
            new TagHelperAttributeList(),
            (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent()));
        output.Content.SetHtmlContent("<canvas id=\"chart-canvas\"></canvas>");

        helper.Process(context, output);
        return output;
    }

    [Fact]
    public void WidgetId_emits_data_sb_widget_so_the_live_bridge_can_see_the_widget()
    {
        var output = Render(new SbWidgetTagHelper
        {
            Width = "half",
            Height = "tall",
            Heading = "Hits per period",
            WidgetId = "time-chart",
            Depends = "summary",
        });

        Assert.Equal("time-chart", output.Attributes["data-sb-widget"].Value);
        Assert.Equal("summary", output.Attributes["data-sb-depends"].Value);
    }

    [Fact]
    public void No_widget_id_omits_the_attribute_so_existing_callers_are_unaffected()
    {
        var output = Render(new SbWidgetTagHelper { Width = "full", Height = "tall" });

        Assert.Null(output.Attributes.SingleOrDefault(a => a.Name == "data-sb-widget"));
    }

    [Fact]
    public void Widget_id_is_emitted_on_the_warming_and_empty_paths_too()
    {
        // The attribute is stamped before the three content branches, so a widget that
        // renders its generating/empty state is still refreshable — otherwise a widget
        // that cold-missed on first paint would be stuck in that state forever (the
        // exact failure the beacon's warm-replace exists to fix).
        foreach (var helper in new[]
                 {
                     new SbWidgetTagHelper { WidgetId = "time-chart", WarmingWhen = true },
                     new SbWidgetTagHelper { WidgetId = "time-chart", EmptyWhen = true },
                 })
        {
            var output = Render(helper);
            Assert.Equal("time-chart", output.Attributes["data-sb-widget"].Value);
        }
    }
}
