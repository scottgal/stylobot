using System.Linq;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class FacetPickerCatalogIconTests
{
    private readonly FacetPickerCatalog _catalog = new();

    [Fact]
    public void Every_Entry_Has_Non_Empty_Icon()
    {
        var missing = _catalog.All.Where(e => string.IsNullOrWhiteSpace(e.Icon)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("ua.family", "cpu-chip")]
    [InlineData("request.path", "globe-alt")]
    [InlineData("geo.country", "map-pin")]
    [InlineData("time.hour_of_day", "clock")]
    [InlineData("risk.score", "chart-bar")]
    [InlineData("attestation.api_key", "key")]
    [InlineData("org.lockdown", "building-office")]
    public void Catalogued_Facet_Has_Expected_Icon(string facet, string expectedIcon)
    {
        var entry = _catalog.All.SingleOrDefault(e => e.Facet == facet);
        Assert.NotNull(entry);
        Assert.Equal(expectedIcon, entry!.Icon);
    }
}
