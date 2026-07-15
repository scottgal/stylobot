namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Static FOSS-only group registry. Groups + rows declared here are
///     the only top-section rows on every gateway, regardless of which
///     packs are loaded. Packs contribute into the PACKS section (rendered
///     between POLICY and SYSTEM by the nav partial) via
///     <see cref="IDashboardPack" /> -- they cannot inject into these groups.
/// </summary>
public static class FossDashboardGroups
{
    public static IReadOnlyList<DashboardGroup> All { get; } =
    [
        // M2: Overview / Activity collapsed into Traffic; Sessions / Threats
        // collapsed into Visitors. Investigate deleted entirely. Insights
        // collapsed into Traffic. Endpoints renamed to Site. The seven
        // legacy URLs 301 inside StyloBotDashboardMiddleware -- the row
        // entries themselves are gone so they no longer appear in the
        // sidebar or resolve through the registry.
        new DashboardGroup("live", "Live",
        [
            // _Traffic.cshtml is the DashboardShellModel-typed row wrapper that
            // projects the cache into a TrafficPageModel and forwards to the
            // typed Traffic/Index.cshtml landing view. The row registry hands
            // every partial DashboardShellModel; this indirection keeps the
            // landing view's @model TrafficPageModel contract intact across
            // every mount (BasePath = "/dashboard", "/stylobot", "/_stylobot").
            new DashboardRow("traffic",  "Traffic",  "~/Views/StyloBot/Dashboard/_Traffic.cshtml"),
            new DashboardRow("visitors", "Visitors", "~/Views/StyloBot/Dashboard/_Visitors.cshtml"),
        ]),
        new DashboardGroup("investigation", "Investigation",
        [
            new DashboardRow("site", "Site", "~/Views/StyloBot/Dashboard/_Endpoints.cshtml"),
        ]),
        new DashboardGroup("policy", "Policy",
        [
            new DashboardRow("policies", "Policies", "~/Views/StyloBot/Dashboard/_Policies.cshtml"),
        ]),
        new DashboardGroup("system", "System",
        [
            new DashboardRow("configuration", "Configuration", "~/Views/StyloBot/Dashboard/_ConfigurationEditor.cshtml"),
            new DashboardRow("compliance",    "Compliance",    "~/Views/StyloBot/Dashboard/_ComplianceTab.cshtml",    IsCommercialOnly: true),
        ]),
    ];

    /// <summary>
    ///     Legacy rows kept for deep-link compatibility. NOT rendered in the
    ///     nav (IsHidden = true). The 301 from <c>?tab=X</c> still lands on
    ///     these routes so external bookmarks survive the migration.
    /// </summary>
    public static IReadOnlyList<DashboardRow> LegacyHidden { get; } =
    [
        new DashboardRow("countries",    "Countries",    "~/Views/StyloBot/Dashboard/_Countries.cshtml",    IsHidden: true),
        new DashboardRow("identities",   "Identities",   "~/Views/StyloBot/Dashboard/_Identities.cshtml",   IsHidden: true),
        new DashboardRow("clusters",     "Clusters",     "~/Views/StyloBot/Dashboard/_ClustersList.cshtml", IsHidden: true),
        new DashboardRow("threat-intel", "Threat Intel", "~/Views/StyloBot/Dashboard/_ThreatIntel.cshtml",  IsHidden: true),
        new DashboardRow("useragents",   "User Agents",  "~/Views/StyloBot/Dashboard/_UserAgents.cshtml",   IsHidden: true),
        new DashboardRow("routes",       "Routes",       "~/Views/StyloBot/Dashboard/_Routes.cshtml",       IsHidden: true),
    ];
}