using FluentAssertions;
using Mostlylucid.BotDetection.AspNetPack.Ui;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.AspNetPack;

/// <summary>
///     Locks in the contract the commercial pack and the docs depend on:
///     the FOSS read-only ASP.NET pack is registered under the
///     <c>aspnet-pack</c> id and contributes exactly two read-only sub-rows
///     (Routes + Identity). Auth events + Log sink are commercial.
/// </summary>
public class AspNetPackReadOnlyDashboardPackTests
{
    [Fact]
    public void Identity_columns_match_spec()
    {
        IDashboardPack pack = new AspNetPackReadOnlyDashboardPack();

        pack.Id.Should().Be("aspnet-pack");
        pack.Label.Should().Be("ASP.NET Pack");
        pack.Icon.Should().Be("bx bx-server");
    }

    [Fact]
    public void Declares_exactly_routes_and_identity_subrows()
    {
        IDashboardPack pack = new AspNetPackReadOnlyDashboardPack();

        pack.SubRows.Should().HaveCount(2);
        pack.SubRows[0].Should().Be(new DashboardSubRow("routes",   "Routes",   "FossSbAspNetEndpoints"));
        pack.SubRows[1].Should().Be(new DashboardSubRow("identity", "Identity", "FossSbAspNetIdentity"));
    }

    [Fact]
    public void Does_not_declare_any_commercial_subrows()
    {
        // Defence in depth: the FOSS pack must never accidentally claim the
        // log-sink / auth / timeline / receivers slots that belong to the
        // commercial pack -- those routes back commercial-only data stores.
        IDashboardPack pack = new AspNetPackReadOnlyDashboardPack();

        var ids = pack.SubRows.Select(r => r.Id).ToList();
        ids.Should().NotContain(new[] { "auth", "log-sink", "timeline", "service-map", "receivers" });
    }
}
