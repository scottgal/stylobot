using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

[DashboardWidget("top-bots", DatasetKind.BotAggregate)]
public class SbTopBotsViewComponent(
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options,
    // signatureCache is optional: remote-mode dashboard viewer hosts don't
    // register it. Per [[feedback_remote_mode_optional_di]]. Used only to
    // size the fetch -- falls back to a sensible default when absent.
    SignatureAggregateCache? signatureCache = null)
    : ViewComponent
{
    private const int FallbackFetchCount = 200;
    public async Task<IViewComponentResult> InvokeAsync(
        int page = 1,
        int pageSize = 10,
        string sortBy = "default",
        string sortDir = "desc",
        string filter = "bots",
        string widgetId = "topbots",
        string? q = null,
        string? audience = null,
        string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        // If the page composer stashed a DashboardPageResult for this request, read the
        // BotAggregate slice from it directly (zero store calls). Otherwise fall through
        // to the existing self-fetch so VCs rendered on non-composer pages still work.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;

        // A genuine cold miss (no snapshot has EVER been composed for this envelope) --
        // render the warming placeholder instead of falling through to a live store call.
        // Distinct from "pageResult present but BotAggregate null" (composer ran but this
        // page's manifest didn't request BotAggregate), which still self-fetches below.
        if (pageResult is { IsWarming: true })
        {
            return View(new TopBotsListModel
            {
                Bots = [], Page = page, PageSize = pageSize, TotalCount = 0,
                SortField = sortBy, SortDir = sortDir, BasePath = options.Value.BasePath.TrimEnd('/'),
                Filter = filter, WidgetId = widgetId, IsWarming = true,
            });
        }

        // Dashboard-wide domain scope (DI seam). FOSS default returns null (no filter =
        // today's behavior); a commercial impl supplies the operator's selected domains,
        // threaded into the self-fetch store read below. Top Bots was the UNCOVERED 4th list
        // widget: Endpoints / Countries / Visitors already thread this seam; Top Bots didn't,
        // so a domain-scoped render self-fetched UNSCOPED (all-domain) rows and its header
        // counters read MORE than the all-domain view -- an impossible superset (scoped >
        // unscoped). The composed path needs no threading here: every composer that stashes a
        // bundle for this VC (TrafficController / VisitorsController) builds its window with
        // AudienceFilter:"all", and TrafficController also scopes it via its own domainsForQuery,
        // so composed rows are already all-audience (+ domain-scoped where applicable). Matching
        // that with an all-audience, domain-scoped self-fetch is what makes the two paths agree
        // and keeps scoped ⊆ unscoped per bucket (domain filtering only removes rows).
        var scopedDomains = HttpContext?.RequestServices?
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(HttpContext);

        IReadOnlyList<DashboardTopBotEntry> raw;
        if (pageResult?.BotAggregate is { } composedBots)
        {
            raw = composedBots;
        }
        else
        {
            // Always read through the event store -- the in-process SignatureAggregateCache
            // is fresh only on a gateway host (write-through via DetectionBroadcastMiddleware);
            // on a remote-mode dashboard host it pins to startup-warm values and the widget
            // shows stale rows forever. Same approach taken in StyloBotDashboardMiddleware.BuildTopBotsModel
            // and SbWidgetBatchMiddleware.BuildTopBotsModel. Fetching audience=all here gives us
            // a cross-cutting top-N so the widget header (All / Bots / Humans) reflects reality
            // and the audience switcher can filter client-side. Domain scope is threaded so a
            // scoped render subsets the store read (GetTopBotsWindowedAsync WHERE domain IN ...).
            var rangeStart = startTime ?? DateTime.UtcNow.AddHours(-24);
            var rangeEnd   = endTime   ?? DateTime.UtcNow;
            var fetchAudience = string.IsNullOrEmpty(audience) ? "all" : audience;
            raw = await eventStore.GetTopBotsAsync(
                count: signatureCache?.MaxEntries ?? FallbackFetchCount,
                startTime: rangeStart,
                endTime: rangeEnd,
                audienceFilter: fetchAudience,
                domains: scopedDomains);
        }
        // Collapse groupable identities BEFORE counting so the header badges
        // (All / Bots / Humans / Internal) match the collapsed rows shown here AND the
        // Visitors tab, which also counts post-collapse (ProjectAsVisitors -> snapshot).
        // Counting the raw set over-counted -- one entry per fingerprint that resolved to
        // an identity -- so Top Bots read e.g. 66 while Visitors read 45 for the same
        // window and looked like a separate/stale source. One collapse, one identity
        // semantics.
        var collapsed = WidgetRenderHelpers.CollapseGroupableIdentities(raw);
        // See SbWidgetBatchMiddleware.BuildTopBotsModel for the Internal rationale.
        static bool IsInternal(DashboardTopBotEntry e) =>
            string.Equals(e.BotType, "Internal", StringComparison.OrdinalIgnoreCase);
        var publicTraffic = collapsed.Where(b => !IsInternal(b)).ToList();
        var internalCount = collapsed.Count - publicTraffic.Count;
        var bots = publicTraffic.Count(b => b.IsKnownBot);
        var humans = publicTraffic.Count - bots;

        IEnumerable<DashboardTopBotEntry> filtered = filter switch
        {
            "bots"     => publicTraffic.Where(b => b.IsKnownBot),
            "humans"   => publicTraffic.Where(b => !b.IsKnownBot),
            "internal" => collapsed.Where(IsInternal),
            _          => publicTraffic
        };
        var sorted = WidgetRenderHelpers.SortTopBots(filtered, sortBy, sortDir).ToList();
        var grouped = WidgetRenderHelpers.ApplySearchFilter(sorted, q);
        var pagedBots = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return View(new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = grouped.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = options.Value.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId,
            Counts = new TopBotsCounts(All: publicTraffic.Count, Bots: bots, Humans: humans, Internal: internalCount),
            Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        });
    }
}
