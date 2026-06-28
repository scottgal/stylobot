namespace Mostlylucid.BotDetection.UI.Services.Dashboard;

public enum DashboardContributionSlot
{
    TrafficCard,
    VisitorDetailSection,
    SiteEndpointDetailSection,
}

/// <summary>
///     Marker interface for pack-registered dashboard contributions. Each pack
///     registers one instance per slot it wants to contribute to via
///     <see cref="DashboardContributionExtensions.AddDashboardContribution{T}"/>.
///     The aggregate Razor view resolves the registry, walks the contributions
///     for its slot, and invokes each via the named ViewComponent. Packs that
///     aren't loaded simply don't appear — graceful absence per
///     <c>feedback_remote_mode_optional_di</c>.
/// </summary>
public interface IDashboardContribution
{
    /// <summary>Which aggregate slot this contribution renders into.</summary>
    DashboardContributionSlot Slot { get; }

    /// <summary>Human-readable provenance, shown alongside the contribution
    /// e.g. "from ASP.NET Pack".</summary>
    string ContributionName { get; }

    /// <summary>The ViewComponent name the registry passes to
    /// <c>IViewComponentHelper.InvokeAsync</c>. The pack's view component
    /// receives whatever arguments the aggregate view passes (e.g.
    /// fingerprintId for VisitorDetailSection).</summary>
    string ViewComponentName { get; }

    /// <summary>Sort key; lower renders first. Defaults are conventional:
    /// 100 ASP.NET Pack, 200 OTel Mesh, etc.</summary>
    int Order { get; }
}
