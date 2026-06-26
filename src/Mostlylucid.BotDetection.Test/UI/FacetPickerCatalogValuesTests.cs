using System.Linq;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pin tests for the C-UX4 value-injection layer: the YAML carries the
///     entry shape, the catalog injects the value list at construction time.
///     If either side drifts (YAML adds a stale values list, country code
///     constant shrinks below the ISO standard, autocomplete source forgets
///     to populate verifiedbot-names), these tests fail fast.
/// </summary>
public sealed class FacetPickerCatalogValuesTests
{
    private readonly FacetPickerCatalog _catalog = new();

    [Fact]
    public void Country_code_enum_has_at_least_240_values()
    {
        var entry = _catalog.FindByFacet("geo.country_code");
        Assert.NotNull(entry);
        Assert.Equal("enum", entry!.Type);
        // ISO 3166-1 alpha-2 carries 249 officially-assigned codes; assert a
        // safety floor so a regression that drops the static-injection step
        // (leaving the YAML's empty list) surfaces immediately.
        Assert.True(entry.Values.Count >= 240,
            $"Expected >=240 country codes, got {entry.Values.Count}");
        // Spot-check: a few canonical codes must be present.
        Assert.Contains("US", entry.Values);
        Assert.Contains("GB", entry.Values);
        Assert.Contains("JP", entry.Values);
        Assert.Contains("ZW", entry.Values);
    }

    [Fact]
    public void Verifiedbot_name_datalist_resolves_from_well_known_bots()
    {
        // The picker catalog tags the verifiedbot.name entry with
        // datalist: verifiedbot-names; the autocomplete source must
        // resolve that key to a non-empty list pulled from the FOSS
        // bot-pattern + arcjet catalogs.
        var entry = _catalog.FindByFacet("verifiedbot.name");
        Assert.NotNull(entry);
        Assert.Equal("verifiedbot-names", entry!.DatalistKey);

        var source = new FacetAutocompleteSource();
        var options = source.GetOptions("verifiedbot-names");
        Assert.NotNull(options);
        Assert.NotEmpty(options!);
        // Canonical names from the curated YAML catalog must appear.
        Assert.Contains(options!, o =>
            string.Equals(o, "Googlebot", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Autocomplete_source_returns_null_for_unknown_key()
    {
        var source = new FacetAutocompleteSource();
        Assert.Null(source.GetOptions("not-a-real-datalist"));
    }
}
