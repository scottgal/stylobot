using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class WidgetRenderHelpersInjectOobTests
{
    [Fact]
    public void Legacy_html_without_data_region_gets_outerHTML_oob_on_root()
    {
        const string html = "<div id=\"my-widget\" data-sb-widget=\"my-widget\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"true\"", result);
        // Attribute lands on the root tag (before the closing >).
        var firstTagEnd = result.IndexOf('>');
        Assert.Contains("hx-swap-oob", result[..firstTagEnd]);
    }

    [Fact]
    public void Html_with_data_region_gets_innerHTML_oob_on_region_not_root()
    {
        const string html = """
            <div id="my-widget" data-sb-widget="my-widget">
              <div class="toolbar">chrome</div>
              <div id="my-widget-data" data-sb-data-region>
                <table><tr><td>row</td></tr></table>
              </div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"innerHTML\"", result);
        // The root <div id="my-widget"> must NOT have hx-swap-oob.
        var firstTagEnd = result.IndexOf('>');
        Assert.DoesNotContain("hx-swap-oob", result[..firstTagEnd]);
        // The data region <div id="my-widget-data"> MUST have it.
        var dataRegionStart = result.IndexOf("id=\"my-widget-data\"");
        var dataRegionTagEnd = result.IndexOf('>', dataRegionStart);
        Assert.Contains("hx-swap-oob=\"innerHTML\"", result[dataRegionStart..dataRegionTagEnd]);
    }

    [Fact]
    public void Already_oob_html_is_left_alone()
    {
        const string html = "<div id=\"my-widget\" hx-swap-oob=\"true\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        // Should not double-inject.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result, "hx-swap-oob").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Data_region_with_existing_oob_is_left_alone()
    {
        const string html = """
            <div id="my-widget">
              <div id="my-widget-data" data-sb-data-region hx-swap-oob="innerHTML">rows</div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result, "hx-swap-oob").Count;
        Assert.Equal(1, occurrences);
    }
}
