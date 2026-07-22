using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbVisitorListViewComponent(
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    // Mirrors the cap used by ServeVisitorListPartialAsync / RenderVisitorPartialAsync
    // so all three Visitors-tab entry points see the same source data slice.
    private const int MaxEntries = 500;

    public async Task<IViewComponentResult> InvokeAsync(
        string filter = "all",
        string sort = "lastSeen",
        string dir = "desc",
        int page = 1,
        int pageSize = 24,
        string? country = null,
        string? botType = null,
        string? threat = null,
        string? fingerprintId = null,
        bool @internal = false)
    {
        var now = DateTime.UtcNow;
        var start = now.AddHours(-24);

        // Read through the event store so remote-mode hosts (no in-process
        // DetectionBroadcastMiddleware feeding the local cache) still get fresh
        // data. ProjectAsVisitors mirrors ServeVisitorListPartialAsync so the
        // SSR pass and the HTMX partial swap render the same rows.
        var pageResult = HttpContext.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        IReadOnlyList<DashboardTopBotEntry> raw;
        if (pageResult?.BotAggregate is { } composedAggregate)
        {
            raw = composedAggregate;
        }
        else
        {
            try
            {
                raw = await eventStore.GetTopBotsAsync(
                    count: MaxEntries,
                    startTime: start,
                    endTime: now,
                    audienceFilter: "all");
            }
            catch
            {
                // Graceful degradation: an empty list still renders a valid (if
                // momentarily rowless) visitor list instead of taking down the
                // whole Visitors/Traffic page the component is embedded in.
                raw = [];
            }
        }
        var (items, total, counts) = WidgetRenderHelpers.ProjectAsVisitors(
            raw, filter, sort, dir, page, pageSize,
            country: country, botType: botType, threat: threat,
            fingerprintId: fingerprintId, internalOnly: @internal);

        // Fetch authoritative segment counts from the store (single source of truth
        // for both tabs and summary). This replaces the parasitic dual-query approach.
        // ALWAYS fetch it -- pageResult (when present) only supplies the raw visitor
        // ROWS (BotAggregate), a windowed/capped list; it says nothing about the true
        // segment totals, so gating this fetch on "no composed bundle" silently served
        // the cheap row-count projection as if it were the authoritative aggregate.
        FilterCounts accurateCounts = counts;
        try
        {
            var segmentCounts = await eventStore.GetVisitorSegmentCountsAsync(
                start, now,
                filter: filter, country: country, botType: botType, threat: threat);
            if (segmentCounts is not null)
                accurateCounts = segmentCounts;
        }
        catch
        {
            // Graceful degradation: fall back to projection counts
        }

        // Plan task 19: same gateway-projected drift badge enrichment the
        // dedicated visitor-list endpoints run. No-ops on a remote-mode host
        // that didn't register IFingerprintReader (marketing site embed),
        // so the badge surfaces on the gateway and gracefully disappears
        // where the data isn't available.
        await FingerprintDriftProjector.EnrichVisitorsAsync(
            items, HttpContext.RequestServices, HttpContext.RequestAborted);

        return View(new VisitorListModel
        {
            Visitors = items,
            Counts = accurateCounts,
            Filter = filter,
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            BasePath = options.Value.BasePath.TrimEnd('/'),
            Country = country,
            BotType = botType,
            Threat = threat,
            FingerprintId = fingerprintId,
            Internal = @internal,
        });
    }
}
