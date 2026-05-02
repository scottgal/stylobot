using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSessionsListViewComponent(
    ISessionStore sessionStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string? filter = null,
        int page = 1,
        int pageSize = 25)
    {
        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };

        // Fetch a large recent set then paginate in-memory - the store has no server-side pagination.
        var allSessions = await sessionStore.GetRecentSessionsAsync(200, isBot);

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
            BotName = s.BotName,
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
