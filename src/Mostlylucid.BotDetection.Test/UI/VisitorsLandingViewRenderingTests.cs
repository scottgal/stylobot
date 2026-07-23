using System.Diagnostics;
using Mostlylucid.BotDetection.Test.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Razor page regression for the middleware Visitors route. A populated Traffic
///     bundle must render the intended Visitors DOM, rather than Detection pending,
///     an empty list, missing radar shapes, or a missing country map.
/// </summary>
public sealed class VisitorsLandingViewRenderingTests
{
    [Fact]
    public async Task Populated_traffic_bundle_renders_all_visitors_surfaces()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DiagnosticSource>(new DiagnosticListener("VisitorsLandingViewRenderingTests"));
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener("VisitorsLandingViewRenderingTests"));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(VisitorsLandingViewRenderingTests).Assembly)
            .AddApplicationPart(typeof(Mostlylucid.BotDetection.UI.ViewComponents.Dashboard.SbVisitorListViewComponent).Assembly);
        services.AddSingleton<RazorViewRenderer>();
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }));
        services.AddSingleton<IOptions<DashboardMaterializerOptions>>(
            Options.Create(new DashboardMaterializerOptions()));
        services.AddSingleton(new Mock<IDashboardEventStore>().Object);
        services.AddSingleton<IFingerprintNameActionSlot, EmptyFingerprintNameActionSlot>();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/dashboard/visitors";
        context.Items["sb.dashboard.pageresult"] = new DashboardPageResult(
            new DashboardDatasetBundle(
                Summary: new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow,
                    TotalRequests = 42,
                    BotRequests = 12,
                    HumanRequests = 30,
                    UncertainRequests = 0,
                    UniqueSignatures = 7,
                    BotFingerprints = 3,
                    HumanFingerprints = 4,
                    RiskBandCounts = new(),
                    TopBotTypes = new(),
                    TopActions = new()
                },
                TimeBuckets: null,
                BotAggregate:
                [
                    new DashboardTopBotEntry
                    {
                        PrimarySignature = "visitor-1",
                        HitCount = 6,
                        BotName = "Example visitor",
                        LastSeen = DateTime.UtcNow,
                        BotProbability = 0.1
                    }
                ],
                Geo:
                [
                    new DashboardCountryStats
                    {
                        CountryCode = "GB",
                        TotalCount = 42,
                        BotCount = 12
                    }
                ],
                Endpoints: null));

        var model = new VisitorsPageModel(
            Filter: "all",
            Country: null,
            BotType: null,
            Threat: null,
            FingerprintId: null,
            Internal: false,
            BasePath: "/dashboard",
            Counters: new TrafficCounters(42, 30, 12, 12d / 42d, 0, 0, 0, 0, 0, 0, 7, 4, 3),
            Countries:
            [
                new DashboardCountryStats
                {
                    CountryCode = "GB",
                    TotalCount = 42,
                    BotCount = 12
                }
            ]);

        var html = await provider.GetRequiredService<RazorViewRenderer>()
            .RenderViewToStringAsync(
                "/Views/StyloBot/Dashboard/Visitors/Index.cshtml",
                model,
                context);

        Assert.Contains("Visitors", html);
        Assert.Contains("42", html);
        Assert.Contains("visitor-1", html);
        Assert.Contains("countries-map-visitors", html);
        // The map init script must be a plain inline <script>, not @section Scripts --
        // this view is always rendered as a nested Html.PartialAsync (never a
        // top-level View() result via the registry path), and @section content is
        // silently dropped unless a Layout calls RenderSectionAsync for it. A
        // regression back to @section would leave the div rendered with real data
        // but jsVectorMap never initialized against it -- the map would be blank
        // with a clean console, exactly as reported on staging.
        Assert.Contains("initMapFromData('countries-map-visitors')", html);
        Assert.Contains("Representative behavioural fingerprint", html);
        Assert.Contains("95% bot", html);
        Assert.Contains("8% bot", html);
        Assert.DoesNotContain("Detection pending", html);
    }
}
