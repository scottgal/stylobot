using Mostlylucid.BotDetection.UI.Middleware;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins the OOB attribute that <see cref="WidgetRenderHelpers.InjectOobAttribute"/>
///     stamps onto live-update widget chunks. Updated for the Idiomorph
///     migration (commits <c>547e1a5</c> + <c>e0fcb60</c>): both the
///     data-region path and the legacy-root fallback now emit the bare
///     <c>hx-swap-oob="morph"</c> keyword. The previous <c>"true"</c> /
///     <c>"innerHTML"</c> values are no longer correct -- Idiomorph's OOB
///     hook in 0.7.4 only matches the unqualified <c>morph</c> keyword;
///     anything else triggers <c>htmx:oobErrorNoTarget</c>.
/// </summary>
public class WidgetRenderHelpersInjectOobTests
{
    [Fact]
    public void Legacy_html_without_data_region_gets_morph_oob_on_root()
    {
        const string html = "<div id=\"my-widget\" data-sb-widget=\"my-widget\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"morph\"", result);
        // Attribute lands on the root tag (before the closing >).
        var firstTagEnd = result.IndexOf('>');
        Assert.Contains("hx-swap-oob", result[..firstTagEnd]);
    }

    [Fact]
    public void Html_with_data_region_gets_morph_oob_on_region_not_root()
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
        Assert.Contains("hx-swap-oob=\"morph\"", result);
        // The root <div id="my-widget"> must NOT have hx-swap-oob.
        var firstTagEnd = result.IndexOf('>');
        Assert.DoesNotContain("hx-swap-oob", result[..firstTagEnd]);
        // The data region <div id="my-widget-data"> MUST have it.
        var dataRegionStart = result.IndexOf("id=\"my-widget-data\"");
        var dataRegionTagEnd = result.IndexOf('>', dataRegionStart);
        Assert.Contains("hx-swap-oob=\"morph\"", result[dataRegionStart..dataRegionTagEnd]);
    }

    [Fact]
    public void Already_oob_html_is_left_alone()
    {
        const string html = "<div id=\"my-widget\" hx-swap-oob=\"morph\">stuff</div>";
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
              <div id="my-widget-data" data-sb-data-region hx-swap-oob="morph">rows</div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        var occurrences = System.Text.RegularExpressions.Regex.Matches(result, "hx-swap-oob").Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Data_region_with_explicit_value_still_gets_morph_oob()
    {
        const string html = """
            <div id="my-widget">
              <div id="my-widget-data" data-sb-data-region="rows">
                <table><tr><td>row</td></tr></table>
              </div>
            </div>
            """;
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"morph\"", result);
        // The data region's existing attributes must survive intact.
        Assert.Contains("data-sb-data-region=\"rows\"", result);
        // Root tag should NOT have OOB injected on it.
        var firstTagEnd = result.IndexOf('>');
        Assert.DoesNotContain("hx-swap-oob", result[..firstTagEnd]);
    }

    [Fact]
    public void Lookalike_attribute_name_does_not_match_data_region()
    {
        // data-sb-data-region-inner is a HYPOTHETICAL sibling attribute. The regex
        // must not treat it as data-sb-data-region; the legacy path should run
        // and stamp morph on the root.
        const string html = "<div id=\"x\" data-sb-data-region-inner=\"y\">stuff</div>";
        var result = WidgetRenderHelpers.InjectOobAttribute(html);
        Assert.Contains("hx-swap-oob=\"morph\"", result);
    }
}
