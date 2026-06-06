using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public class IDashboardPackContractTests
{
    [Fact]
    public void DashboardSubRow_record_equality_holds_on_value_semantics()
    {
        var a = new DashboardSubRow("log-sink", "Log sink", "SbAspNetLogSink");
        var b = new DashboardSubRow("log-sink", "Log sink", "SbAspNetLogSink");
        a.Should().Be(b);
    }

    [Fact]
    public void IDashboardPack_minimal_impl_exposes_required_surface()
    {
        IDashboardPack pack = new MinimalPack();
        pack.Id.Should().Be("aspnet-pack");
        pack.Label.Should().Be("ASP.NET Pack");
        pack.Icon.Should().Be("bx bx-server");
        pack.SubRows.Should().HaveCount(1);
        pack.SubRows[0].ViewComponentName.Should().Be("SbAspNetRoutes");
    }

    private sealed class MinimalPack : IDashboardPack
    {
        public string Id => "aspnet-pack";
        public string Label => "ASP.NET Pack";
        public string Icon => "bx bx-server";
        public IReadOnlyList<DashboardSubRow> SubRows { get; } =
            [new DashboardSubRow("routes", "Routes", "SbAspNetRoutes")];
    }
}
