using Microsoft.AspNetCore.Razor.TagHelpers;
using Mostlylucid.BotDetection.UI.TagHelpers.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Dashboard-graph-quality PART 1: the &lt;sb-widget&gt; shell must be able to
///     paint a THREE-state contract for charts (real data / warming spinner / honest
///     empty), matching the house pattern the list widgets already use
///     (SbCountriesList / SbSummaryStats). Before this, a chart mounted with no guard
///     rendered a bare Chart.js canvas on a cold cache. This pins the new
///     warming-when/warming-text strip and its precedence over empty-when.
/// </summary>
public sealed class SbWidgetTagHelperWarmingTests
{
    private static (string rendered, TagHelperOutput output) Render(SbWidgetTagHelper helper)
    {
        var context = new TagHelperContext(
            tagName: "sb-widget",
            allAttributes: new TagHelperAttributeList(),
            items: new Dictionary<object, object>(),
            uniqueId: "test");
        var output = new TagHelperOutput(
            "sb-widget",
            new TagHelperAttributeList(),
            (_, _) =>
            {
                var c = new DefaultTagHelperContent();
                c.SetHtmlContent("<canvas id=\"chart-canvas\"></canvas>");
                return Task.FromResult<TagHelperContent>(c);
            });
        // Prime Content with the child body so the wrap path has something to wrap.
        output.Content.SetHtmlContent("<canvas id=\"chart-canvas\"></canvas>");

        helper.Process(context, output);

        var rendered = output.PreContent.GetContent()
            + output.Content.GetContent()
            + output.PostContent.GetContent();
        return (rendered, output);
    }

    [Fact]
    public void WarmingWhen_true_renders_the_honest_empty_strip_and_suppresses_the_canvas()
    {
        // P0 2026-08-16 (the operator's "period switch spinner"): the warming spinner
        // is DEAD — NO loading state ever. A cold-cache miss renders the honest empty
        // strip (the warming-when text when present); the background prewarm fills the
        // envelope and the beacon OOB-replaces the widget once warm.
        var helper = new SbWidgetTagHelper
        {
            Width = "half",
            Height = "tall",
            Heading = "Hits per period",
            WarmingWhen = true,
            WarmingText = "Warming up — chart data will appear shortly.",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("Warming up", rendered);
        Assert.DoesNotContain("loading loading-spinner", rendered);
        // The bare Chart.js canvas MUST NOT render behind the empty strip.
        Assert.DoesNotContain("chart-canvas", rendered);
    }

    [Fact]
    public void WarmingWhen_takes_precedence_over_EmptyWhen_without_a_spinner()
    {
        var helper = new SbWidgetTagHelper
        {
            WarmingWhen = true,
            WarmingText = "Warming up — chart data will appear shortly.",
            EmptyWhen = true,
            EmptyText = "No data in this window",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("Warming up", rendered);
        Assert.DoesNotContain("loading loading-spinner", rendered);
        Assert.DoesNotContain("No data in this window", rendered);
    }

    [Fact]
    public void EmptyWhen_without_warming_still_renders_the_empty_strip()
    {
        var helper = new SbWidgetTagHelper
        {
            EmptyWhen = true,
            EmptyText = "No data in this window",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("No data in this window", rendered);
        Assert.DoesNotContain("loading loading-spinner", rendered);
        Assert.DoesNotContain("chart-canvas", rendered);
    }

    [Fact]
    public void Neither_warming_nor_empty_wraps_the_child_canvas()
    {
        var helper = new SbWidgetTagHelper
        {
            Heading = "Hits per period",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("chart-canvas", rendered);
        Assert.DoesNotContain("loading loading-spinner", rendered);
    }

    [Fact]
    public void Depends_stamps_data_sb_depends_on_the_outer_element()
    {
        var helper = new SbWidgetTagHelper
        {
            Depends = "summary",
        };

        var (_, output) = Render(helper);

        Assert.Equal("summary", output.Attributes["data-sb-depends"].Value?.ToString());
    }
}
