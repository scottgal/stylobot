using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="SignatureDisplayName.Display"/> -- the ONLY name-render
///     entry point for every dashboard list / detail view. No UA parse, no
///     catalog lookup, no fallback chain. Operator override > upstream-set
///     BotName > the explicit "—" placeholder. Anything more clever belongs
///     UPSTREAM in the matcher / store / broadcast canonicaliser, not here.
/// </summary>
public class SignatureDisplayNameTests
{
    [Fact]
    public void Display_returns_botname_when_set()
    {
        Assert.Equal("Googlebot", SignatureDisplayName.Display("Googlebot"));
    }

    [Fact]
    public void Display_returns_custom_label_over_botname()
    {
        // Operator rename always wins -- the customLabel chokepoint covers the
        // case where a UI rename should override whatever the matcher landed.
        Assert.Equal("Customer FX scraper",
            SignatureDisplayName.Display(botName: "Googlebot", customLabel: "Customer FX scraper"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Display_falls_back_to_placeholder_when_botname_blank(string? botName)
    {
        Assert.Equal(SignatureDisplayName.Unnamed, SignatureDisplayName.Display(botName));
    }

    [Fact]
    public void Display_trims_botname()
    {
        Assert.Equal("Googlebot", SignatureDisplayName.Display("  Googlebot  "));
    }

    [Fact]
    public void Placeholder_is_the_em_dash_data_cell_convention()
    {
        // Em-dash matches the "no value" convention used elsewhere in the
        // dashboard's data cells (UA / VER columns). Operator can tell at a
        // glance the upstream pipeline hasn't named the row yet -- not a
        // weasel "Unknown", not a hash prefix.
        Assert.Equal("—", SignatureDisplayName.Unnamed);
    }

    [Fact]
    public void TitleAttr_exposes_full_signature_for_incident_notes()
    {
        // The full hash is reachable via the row's title attribute so the
        // operator can grep / paste it without seeing it on the visible label.
        Assert.Equal("Signature: 6GvWI2ZWu-3e2Kh5ybioBQ",
            SignatureDisplayName.TitleAttr("6GvWI2ZWu-3e2Kh5ybioBQ"));
    }
}