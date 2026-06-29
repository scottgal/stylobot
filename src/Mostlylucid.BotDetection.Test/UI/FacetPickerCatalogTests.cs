using FluentAssertions;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class FacetPickerCatalogTests
{
    [Fact]
    public void All_loads_more_than_20_entries_from_embedded_yaml()
    {
        var catalog = new FacetPickerCatalog();
        catalog.All.Count.Should().BeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public void Every_entry_has_label_facet_type_ops_help()
    {
        var catalog = new FacetPickerCatalog();
        foreach (var entry in catalog.All)
        {
            entry.Label.Should().NotBeNullOrWhiteSpace();
            entry.Facet.Should().NotBeNullOrWhiteSpace();
            entry.Type.Should().BeOneOf("enum", "number", "bool", "string", "glob", "cidr");
            entry.Ops.Should().NotBeEmpty();
            entry.Help.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("ua.bot_type")]
    [InlineData("ua.is_bot")]
    [InlineData("risk.score")]
    [InlineData("geo.country_code")]
    [InlineData("request.path")]
    [InlineData("time.hour_of_day")]
    [InlineData("org.lockdown")]
    public void Cornerstone_facets_present(string facet)
    {
        var catalog = new FacetPickerCatalog();
        catalog.FindByFacet(facet).Should().NotBeNull("the curated picker must surface " + facet);
    }

    [Fact]
    public void Enum_entries_have_values_or_external_source()
    {
        // Either inline values (most enums) or an empty values list (e.g.
        // country codes, where the picker UI renders a search input keyed off
        // a wider source).
        var catalog = new FacetPickerCatalog();
        foreach (var enumEntry in catalog.All.Where(e => e.Type == "enum"))
        {
            enumEntry.Values.Should().NotBeNull();
        }
    }

    [Fact]
    public void Ip_address_facet_present_with_in_cidr_op_and_cidr_type()
    {
        // A3 of the bundles + config editor plan: the new in_cidr predicate
        // op (PredicateOp.InCidr, wire-form "in_cidr") needs a webmaster-
        // facing surface. The ip.address facet matches the canonical
        // SignalKeys.ClientIp ("ip.address") used by the evaluator + the
        // PredicateOpRoundTripTests fixture.
        var catalog = new FacetPickerCatalog();
        var entry = catalog.FindByFacet("ip.address");
        entry.Should().NotBeNull("picker must surface ip.address so webmasters can author in_cidr rules");
        entry!.Type.Should().Be("cidr");
        entry.Ops.Should().Contain("in_cidr");
        entry.Ops.Should().Contain("eq");
        entry.Ops.Should().Contain("in");
        entry.Category.Should().Be("geo");
        entry.Help.Should().NotBeNullOrWhiteSpace();
    }
}
