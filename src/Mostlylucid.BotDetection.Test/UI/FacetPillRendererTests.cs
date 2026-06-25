using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class FacetPillRendererTests
{
    private readonly FacetPillRenderer _r = new(new FacetPickerCatalog());

    [Fact]
    public void Catalogued_Facet_Returns_Labelled_Pill()
    {
        var pill = _r.Render("ua.family", "eq", "StyloBot.Internal");
        Assert.False(pill.IsUncatalogued);
        Assert.Equal("Bot family", pill.CategoryLabel);
        Assert.Equal("cpu-chip", pill.IconRef);
        Assert.Equal("is", pill.OperatorLabel);
        Assert.Equal("StyloBot.Internal", pill.ValueDisplay);
        Assert.Equal("facet-cat-bot-identity", pill.CategoryClass);
    }

    [Fact]
    public void Uncatalogued_Facet_Falls_Back_To_Raw()
    {
        var pill = _r.Render("custom.weird_key", "eq", "X");
        Assert.True(pill.IsUncatalogued);
        Assert.Equal("custom.weird_key", pill.CategoryLabel);
        Assert.Equal("tag", pill.IconRef);
    }

    [Theory]
    [InlineData("eq", "is")]
    [InlineData("in", "is one of")]
    [InlineData("gte", "≥")]
    [InlineData("matches", "matches")]
    public void Operator_Label_Mapping(string op, string expected)
    {
        var pill = _r.Render("ua.family", op, "X");
        Assert.Equal(expected, pill.OperatorLabel);
    }
}
