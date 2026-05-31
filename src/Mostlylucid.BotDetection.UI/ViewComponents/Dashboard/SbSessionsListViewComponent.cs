using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSessionsListViewComponent(
    ISessionStore sessionStore,
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
        int pageSize = 25)
    {
        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };

        // Fetch only enough sessions for the current and any future pages the user is likely to reach.
        // The store has no server-side pagination, so we over-fetch conservatively.
        var fetchCount = Math.Min((page * pageSize) + pageSize, 200);
        var allSessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot);

        var sigLookup = await eventStore.LoadSignatureLookupAsync();

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

        var allEntries = allSessions.Select(s =>
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
            Filter = filter
        });
    }
}
