using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSessionsListViewComponent(
    IDetectionArchive sessionStore,
    // signatureCache is optional: remote-mode dashboard viewer hosts don't
    // register it (detection runs on the gateway; the viewer reads via REST).
    // Hard-required ctor injection would 500 the whole Sessions tab on the
    // website. Per [[feedback_remote_mode_optional_di]].
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options,
    SignatureAggregateCache? signatureCache = null)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string? filter = null,
        int page = 1,
        int pageSize = 25,
        // V2 of dashboard IA collapse: when the visitor detail page embeds this
        // component as the "Hit history" panel it passes the visitor's primary
        // signature so the timeline is scoped to one identity. Null = unchanged
        // global timeline (existing call sites: SbWidgetBatchMiddleware,
        // StyloBotDashboardMiddleware overview render, SbSessionsListTagHelper).
        // The bot/human filter pills are hidden in scoped mode (a single
        // signature is one or the other already) but still honoured if passed.
        string? primarySignature = null)
    {
        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };

        // If the page composer stashed a DashboardPageResult for this request, read the
        // Sessions slice from it directly (zero store calls) -- but ONLY for the unscoped
        // global timeline. A primarySignature-scoped embed (visitor detail's "Hit history"
        // panel) needs a DIFFERENT per-signature query the composed row extra doesn't cover
        // (DashboardRowRawFetchers.FetchSessionsRawAsync always fetches the unscoped
        // recent-sessions view), so it always falls through to the existing self-fetch.
        // Composed entries already carry resolved BotName/UserAgent (the composer runs the
        // SAME sigLookup/uaLookup resolution the self-fetch path below does) but do not
        // compute Paths/ScoreDeltaPp -- an accepted Stage 2a limitation shared with the
        // row-dispatch path (StyloBotDashboardMiddleware.BuildSessionsModelFromRaw).
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;

        // A genuine cold miss -- render the warming placeholder instead of falling through
        // to a live store call. See SbTopBotsViewComponent for the same guard's rationale.
        // Still applies to the primarySignature-scoped embed: a Warming pageResult means no
        // snapshot exists for THIS envelope yet, independent of the scoping question below.
        if (pageResult is { IsWarming: true })
        {
            return View(new SessionsListModel
            {
                Sessions = [], BasePath = options.Value.BasePath.TrimEnd('/'), Page = page, PageSize = pageSize,
                TotalCount = 0, Filter = filter, PrimarySignature = primarySignature, IsWarming = true,
            });
        }

        List<SessionListEntry> allEntries;
        if (string.IsNullOrEmpty(primarySignature) && pageResult?.SessionsRaw is { } composedSessions)
        {
            allEntries = isBot.HasValue
                ? composedSessions.Where(e => e.IsBot == isBot.Value).ToList()
                : composedSessions.ToList();
        }
        else
        {
            // Fetch only enough sessions for the current and any future pages the user is likely to reach.
            // The store has no server-side pagination, so we over-fetch conservatively.
            var fetchCount = Math.Min((page * pageSize) + pageSize, 200);
            var since = DateTime.UtcNow - options.Value.DetectionRetention;
            List<Mostlylucid.BotDetection.Data.PersistedSession> allSessions;
            if (!string.IsNullOrEmpty(primarySignature))
            {
                // Scoped read uses the per-signature path. IDetectionArchive.GetSessionsAsync
                // returns most-recent-first already; the bot/human filter is a post-
                // filter so the page-size math still applies.
                var scoped = await sessionStore.GetSessionsAsync(primarySignature, fetchCount);
                allSessions = isBot.HasValue
                    ? scoped.Where(s => s.IsBot == isBot.Value).ToList()
                    : scoped;
            }
            else
            {
                allSessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot, since);
            }

            var sigLookup = await eventStore.LoadSignatureLookupAsync();
            var uaLookup  = await eventStore.LoadUserAgentLookupAsync();

            // GetRecentSessionsAsync returns DESC by StartedAt. To compute a per-row
            // score-delta we need the *next* (older) session for the same signature.
            // Build a per-signature timeline ASC once, then look up the prior entry
            // by Id in the visible window.
            var priorProbBySessionId = new Dictionary<long, double>();
            foreach (var group in allSessions.GroupBy(s => s.Signature))
            {
                var asc = group.OrderBy(s => s.StartedAt).ToList();
                for (var i = 1; i < asc.Count; i++)
                {
                    priorProbBySessionId[asc[i].Id] = asc[i - 1].AvgBotProbability;
                }
            }

            allEntries = allSessions.Select(s =>
            {
                IReadOnlyList<string>? paths = null;
                if (!string.IsNullOrEmpty(s.PathsJson))
                {
                    try
                    {
                        paths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.PathsJson);
                    }
                    catch (System.Text.Json.JsonException) { /* tolerate malformed PathsJson */ }
                }

                double? deltaPp = null;
                if (priorProbBySessionId.TryGetValue(s.Id, out var priorProb))
                {
                    deltaPp = (s.AvgBotProbability - priorProb) * 100.0;
                }

                return new SessionListEntry
                {
                    Id = s.Id,
                    Signature = s.Signature,
                    StartedAt = s.StartedAt,
                    EndedAt = s.EndedAt,
                    RequestCount = s.RequestCount,
                    DominantState = s.DominantState,
                    IsBot = s.IsBot,
                    AvgBotProbability = s.AvgBotProbability,
                    RiskBand = s.RiskBand,
                    Action = s.Action,
                    BotName = sigLookup.ResolveBotName(signatureCache, s.Signature, s.BotName),
                    CountryCode = s.CountryCode,
                    UserAgent = uaLookup.ResolveUserAgent(signatureCache, s.Signature),
                    ErrorCount = s.ErrorCount,
                    TimingEntropy = s.TimingEntropy,
                    Maturity = s.Maturity,
                    TransitionCounts = s.TransitionCountsJson != null
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                        : null,
                    Paths = paths,
                    ScoreDeltaPp = deltaPp
                };
            }).ToList();
        }

        var totalCount = allEntries.Count;
        var pagedEntries = allEntries
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return View(new SessionsListModel
        {
            Sessions = pagedEntries,
            BasePath = options.Value.BasePath.TrimEnd('/'),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Filter = filter,
            PrimarySignature = primarySignature
        });
    }
}
