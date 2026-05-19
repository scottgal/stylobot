using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSessionsListViewComponent(
    ISessionStore sessionStore,
    SignatureAggregateCache signatureCache,
    IOptions<StyloBotDashboardOptions> options)
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

        // PersistedSession.BotName is populated at session-end time and is often null because the
        // bot's English name (e.g. "GPTBot", "wget") is resolved by the signature description
        // pipeline AFTER the session row was written. Enrich the entry from the
        // SignatureAggregateCache (which sees every detection and stores the resolved BotName)
        // so the sessions list renders "GPTBot - 22 req" rather than "DmSCDVKl5Hhm - 22 req".
        string? ResolveBotName(string signature, string? storedName)
        {
            if (!string.IsNullOrEmpty(storedName)) return storedName;
            return signatureCache.TryGet(signature, out var agg) ? agg?.BotName : null;
        }

        var allEntries = allSessions.Select(s => new SessionListEntry
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
            BotName = ResolveBotName(s.Signature, s.BotName),
            CountryCode = s.CountryCode,
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null
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
