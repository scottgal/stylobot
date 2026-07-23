using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
/// The V2 sidebar (the one actually served -- DashboardLayoutOptions.V2Enabled defaults
/// true) used to hardcode its pack-nav list to exactly "aspnet-pack" and "otel-mesh",
/// ignoring DashboardShellModel.Packs (the real DI-registered IDashboardPack
/// collection that the row registry and the legacy _LeftNav.cshtml both already
/// consult). Any OTHER registered pack -- e.g. a commercial Settings or Domains pack
/// -- was reachable by direct URL (the registry resolves it) but never appeared in
/// the left nav: discoverable only if you already knew the URL.
/// </summary>
public sealed class SidebarV2PackNavTests
{
    private sealed class FakePack : IDashboardPack
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string Icon { get; init; } = "bx bx-cog";
        public IReadOnlyList<DashboardSubRow> SubRows { get; init; } = [];
    }

    [Fact]
    public async Task Every_registered_pack_appears_in_the_nav_not_just_aspnet_and_otel()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<DiagnosticSource>(new DiagnosticListener("SidebarV2PackNavTests"));
        services.AddSingleton<DiagnosticListener>(new DiagnosticListener("SidebarV2PackNavTests"));
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(SidebarV2PackNavTests).Assembly)
            .AddApplicationPart(typeof(IDashboardPack).Assembly);
        services.AddSingleton<RazorViewRenderer>();
        services.AddSingleton<IOptions<DashboardLayoutOptions>>(
            Options.Create(new DashboardLayoutOptions { V2Enabled = true }));
        // _SidebarV2.cshtml @injects INavVisibilityPolicy (hidden-nav-links seam); this hand-built
        // container needs the same FOSS default a real host gets via AddStyloBotDashboard.
        // AddOptions<NavVisibilityOptions>() with no binding source is enough to make
        // IOptionsMonitor<NavVisibilityOptions> resolvable with the default (empty HiddenPaths).
        services.AddOptions<NavVisibilityOptions>();
        services.AddSingleton<INavVisibilityPolicy, DefaultNavVisibilityPolicy>();

        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/dashboard/settings";

        var settingsPack = new FakePack { Id = "settings", Label = "Settings" };
        var domainsPack = new FakePack { Id = "domains", Label = "Domains" };

        var model = new DashboardShellModel
        {
            CspNonce = "n",
            BasePath = "/dashboard",
            HubPath = "/dashboard/hub",
            ActiveRow = new DashboardRowRef("settings"),
            Summary = new SummaryStatsModel { Summary = EmptySummary(), BasePath = "/dashboard" },
            Visitors = new VisitorListModel
            {
                Visitors = [], Counts = new FilterCounts(), Filter = "all",
                SortField = "lastSeen", SortDir = "desc", Page = 1, PageSize = 24,
                TotalCount = 0, BasePath = "/dashboard"
            },
            YourDetection = new YourDetectionModel { BasePath = "/dashboard" },
            Countries = new CountriesListModel { Countries = [], BasePath = "/dashboard" },
            Endpoints = new EndpointsListModel { Endpoints = [], BasePath = "/dashboard" },
            Clusters = new ClustersListModel { Clusters = [], BasePath = "/dashboard" },
            UserAgents = new UserAgentsListModel { UserAgents = [], BasePath = "/dashboard" },
            TopBots = new TopBotsListModel
            {
                Bots = [], Page = 1, PageSize = 10, TotalCount = 0,
                SortField = "default", BasePath = "/dashboard"
            },
            Sessions = new SessionsListModel { Sessions = [], BasePath = "/dashboard" },
            Threats = new ThreatsListModel { BasePath = "/dashboard" },
            License = LicenseCardModelBuilder.Build(context, "/dashboard"),
            IsCommercial = true,
            Packs = [settingsPack, domainsPack],
        };

        var html = await provider.GetRequiredService<RazorViewRenderer>()
            .RenderViewToStringAsync(
                "/Views/StyloBot/Dashboard/_SidebarV2.cshtml",
                model,
                context);

        Assert.Contains("Settings", html);
        Assert.Contains("Domains", html);
        Assert.Contains("/dashboard/settings", html);
        Assert.Contains("/dashboard/domains", html);
    }

    private static DashboardSummary EmptySummary() => new()
    {
        Timestamp = DateTime.UtcNow,
        TotalRequests = 0,
        BotRequests = 0,
        HumanRequests = 0,
        UncertainRequests = 0,
        RiskBandCounts = new(),
        TopBotTypes = new(),
        TopActions = new(),
        UniqueSignatures = 0,
    };
}
