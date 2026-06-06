using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public class DashboardRowRegistryTests
{
    [Fact]
    public void Resolve_returns_match_for_known_foss_row()
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());
        var match = sut.Resolve("overview", sub: null);
        match.Should().NotBeNull();
        match!.PartialPath.Should().EndWith("_Overview.cshtml");
        match.ViewComponentName.Should().BeNull();
        match.Pack.Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_for_unknown_row()
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());
        sut.Resolve("does-not-exist", null).Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_match_for_legacy_hidden_row()
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());
        sut.Resolve("countries", null).Should().NotBeNull();
    }

    [Fact]
    public void Resolve_returns_match_for_pack_subrow()
    {
        var sut = new DashboardRowRegistry(new[] { new FakePack("aspnet-pack",
            new DashboardSubRow("log-sink", "Log sink", "SbAspNetLogSink")) });
        var match = sut.Resolve("aspnet-pack", "log-sink");
        match.Should().NotBeNull();
        match!.ViewComponentName.Should().Be("SbAspNetLogSink");
        match.Pack!.Id.Should().Be("aspnet-pack");
        match.PartialPath.Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_null_for_bare_pack_id()
    {
        var sut = new DashboardRowRegistry(new[] { new FakePack("aspnet-pack",
            new DashboardSubRow("routes", "Routes", "SbAspNetRoutes")) });
        sut.Resolve("aspnet-pack", null).Should().BeNull();
    }

    [Fact]
    public void IsCommercialOnly_flag_propagates_for_system_rows()
    {
        var sut = new DashboardRowRegistry(Array.Empty<IDashboardPack>());
        sut.Resolve("compliance", null)!.IsCommercialOnly.Should().BeTrue();
        sut.Resolve("overview",   null)!.IsCommercialOnly.Should().BeFalse();
    }

    [Fact]
    public void Packs_are_exposed_in_registration_order()
    {
        var packs = new IDashboardPack[]
        {
            new FakePack("aspnet-pack", new DashboardSubRow("a", "A", "X")),
            new FakePack("otel-mesh",   new DashboardSubRow("b", "B", "Y")),
        };
        var sut = new DashboardRowRegistry(packs);
        sut.Packs.Select(p => p.Id).Should().Equal("aspnet-pack", "otel-mesh");
    }

    private sealed class FakePack : IDashboardPack
    {
        public FakePack(string id, params DashboardSubRow[] subRows)
        {
            Id = id;
            Label = id;
            Icon = "bx bx-cube";
            SubRows = subRows;
        }

        public string Id { get; }
        public string Label { get; }
        public string Icon { get; }
        public IReadOnlyList<DashboardSubRow> SubRows { get; }
    }
}
