using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

/// <summary>
///     FOSS guardian roster: lists every registered <see cref="IGuardian"/> grouped
///     by category with its latest <see cref="GuardianReport"/> (status, rows
///     before/after, bytes reclaimed). Read-only; the in-app editor for
///     intervals/caps/retention is the commercial surface, FOSS tunes the same
///     values via appsettings.json.
///     <para>
///         <see cref="GuardianService"/> is optional so remote-mode dashboard hosts
///         (a REST viewer with no detection runtime) render a graceful empty state
///         instead of 500ing. Same pattern as the optional caches on the other
///         view components.
///     </para>
/// </summary>
public class SbGuardiansViewComponent(
    IOptions<StyloBotDashboardOptions> options,
    GuardianService? guardianService = null)
    : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var basePath = options.Value.BasePath.TrimEnd('/');

        if (guardianService is null)
            return View(new GuardianRosterModel { BasePath = basePath, Available = false });

        var latest = guardianService.LatestReports;
        var rows = guardianService.Guardians
            .OrderBy(g => g.Category)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                latest.TryGetValue(g.Name, out var r);
                return new GuardianRosterRow
                {
                    Name = g.Name,
                    Category = g.Category.ToString(),
                    IntervalDisplay = FormatInterval(g.Interval),
                    Status = r?.Status,
                    RowsBefore = r?.RowsBefore ?? 0,
                    RowsAfter = r?.RowsAfter ?? 0,
                    BytesReclaimed = r?.BytesReclaimed ?? 0,
                    DurationMs = r?.DurationMs ?? 0,
                    Details = r?.Details,
                    LastRunAt = r?.At
                };
            })
            .ToList();

        return View(new GuardianRosterModel { BasePath = basePath, Available = true, Rows = rows });
    }

    private static string FormatInterval(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#}h"
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#}m"
        : $"{t.TotalSeconds:0}s";
}
