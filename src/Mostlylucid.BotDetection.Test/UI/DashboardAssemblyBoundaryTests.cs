using FluentAssertions;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.Dashboard.Data;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class DashboardAssemblyBoundaryTests
{
    [Fact]
    public void Api_and_data_have_no_reference_to_the_Razor_UI_assembly()
    {
        typeof(StyloBotApiExtensions).Assembly.GetReferencedAssemblies().Should().NotContain(a => a.Name == "Mostlylucid.BotDetection.UI");
        typeof(DashboardDataServiceExtensions).Assembly.GetReferencedAssemblies().Should().NotContain(a =>
            a.Name == "Mostlylucid.BotDetection.UI" || a.Name!.Contains("Razor"));
        typeof(TrafficSnapshot).Assembly.GetReferencedAssemblies().Should().NotContain(a =>
            a.Name!.StartsWith("Microsoft.AspNetCore") || a.Name.StartsWith("Mostlylucid.BotDetection"));
    }

    [Fact]
    public void Api_error_responses_have_generated_JSON_metadata_for_native_hosts()
    {
        var context = StyloBotJsonContext.Default;
        var invalid = new Microsoft.AspNetCore.Http.HttpValidationProblemDetails(new Dictionary<string, string[]> { ["window"] = ["Invalid"] });
        System.Text.Json.JsonSerializer.Serialize(invalid, context.HttpValidationProblemDetails).Should().Contain("Invalid");
        var unavailable = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = 503, Title = "Unavailable" };
        unavailable.Extensions["traceId"] = "reference";
        System.Text.Json.JsonSerializer.Serialize(unavailable, context.ProblemDetails).Should().Contain("reference");
    }

    [Fact]
    public void Old_persistence_and_options_types_are_forwarded_but_pack_discovery_stays_in_UI()
    {
        var ui = typeof(IDashboardPack).Assembly;
        ui.GetForwardedTypes().Should().Contain(typeof(IDashboardEventStore))
            .And.Contain(typeof(SqliteDashboardEventStore)).And.Contain(typeof(StyloBotDashboardOptions));
        ui.GetManifestResourceNames().Should().NotContain(n => n.EndsWith("dashboard_events.sql"));
        typeof(SqliteDashboardEventStore).Assembly.GetManifestResourceNames().Should()
            .Contain("Mostlylucid.BotDetection.UI.Data.Schema.dashboard_events.sql");
        Assert.NotSame(ui, typeof(SqliteDashboardEventStore).Assembly);
    }
}
