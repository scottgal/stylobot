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
    public void WarmingWhen_true_renders_the_generating_state_and_suppresses_the_canvas()
    {
        // Operator 2026-08-19 (supersedes the 4fc31a8c "NO loading state ever"
        // doctrine FOR THE NOT-DATA PATH): a cold-cache miss (period change to a
        // not-yet-composed window) renders the ONE honest "generating" state —
        // spinner + copy — never a "no data" render ("no signal data is NOT a
        // valid state; the signal is always in that period"). The swap region's
        // bounded retry transitions it to data; the copy is never the empty text.
        var helper = new SbWidgetTagHelper
        {
            Width = "half",
            Height = "tall",
            Heading = "Hits per period",
            WarmingWhen = true,
            WarmingText = "Generating 7d…",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("Generating 7d", rendered);
        Assert.Contains("loading loading-spinner", rendered);
        Assert.Contains("sb-widget-loading", rendered);
        // The bare Chart.js canvas MUST NOT render behind the generating state.
        Assert.DoesNotContain("chart-canvas", rendered);
    }

    [Fact]
    public void WarmingWhen_takes_precedence_over_EmptyWhen()
    {
        var helper = new SbWidgetTagHelper
        {
            WarmingWhen = true,
            WarmingText = "Generating 24h…",
            EmptyWhen = true,
            EmptyText = "No data in this window",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("Generating 24h", rendered);
        Assert.Contains("loading loading-spinner", rendered);
        Assert.DoesNotContain("No data in this window", rendered);
    }

    [Fact]
    public void EmptyWhen_without_warming_renders_the_plain_strip()
    {
        // EmptyWhen is ONLY for callers whose "empty" has non-signal semantics
        // (e.g. site-health's "no incidents"); signal-data widgets never use it
        // (they fold composed-empty into the generating state). The plain strip
        // stays spinner-free — it is a terminal state, not a generating one.
        var helper = new SbWidgetTagHelper
        {
            EmptyWhen = true,
            EmptyText = "Upstream healthy — no incidents",
        };

        var (rendered, _) = Render(helper);

        Assert.Contains("Upstream healthy", rendered);
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
