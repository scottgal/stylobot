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
        new DashboardGroup("live", "Live",
        [
            new DashboardRow("overview", "Overview", "~/Views/StyloBot/Dashboard/_Overview.cshtml"),
            new DashboardRow("activity", "Activity", "~/Views/StyloBot/Dashboard/_Activity.cshtml"),
            new DashboardRow("visitors", "Visitors", "~/Views/StyloBot/Dashboard/_Visitors.cshtml"),
        ]),
        new DashboardGroup("investigation", "Investigation",
        [
            new DashboardRow("endpoints", "Endpoints", "~/Views/StyloBot/Dashboard/_Endpoints.cshtml"),
            new DashboardRow("sessions",  "Sessions",  "~/Views/StyloBot/Dashboard/_Sessions.cshtml"),
            new DashboardRow("threats",   "Threats",   "~/Views/StyloBot/Dashboard/_Threats.cshtml"),
        ]),
        new DashboardGroup("policy", "Policy",
        [
            new DashboardRow("policies", "Policies", "~/Views/StyloBot/Dashboard/_Policies.cshtml"),
        ]),
        new DashboardGroup("system", "System",
        [
            new DashboardRow("configuration", "Configuration", "~/Views/StyloBot/Dashboard/_ConfigurationEditor.cshtml"),
            // B1: global meter inventory (mini-grafana index). Lives in System
            // because it's an observability surface, not a Live/Investigation
            // pivot. Sub-rows under /dashboard/insights/{meter-name} are
            // reserved for B2+; the row dispatches the top-level page only.
            new DashboardRow("insights",      "Insights",      "~/Views/StyloBot/Dashboard/_Insights.cshtml"),
            new DashboardRow("compliance",    "Compliance",    "~/Views/StyloBot/Dashboard/_ComplianceTab.cshtml",    IsCommercialOnly: true),
            new DashboardRow("investigate",   "Investigate",   "~/Views/StyloBot/Dashboard/_Investigate.cshtml",      IsCommercialOnly: true),
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
