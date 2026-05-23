using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Middleware;

internal static class WidgetRenderHelpers
{
    // ^\s* tolerates leading whitespace/newlines that Razor emits before the first tag
    private static readonly Regex FirstTagRegex = new(
        @"^\s*(<[a-zA-Z][^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Matches the first opening tag that carries the data-sb-data-region attribute.
    // Group 1 = "<tag ... " (everything before the trailing >). Group 2 = "/>" or ">".
    private static readonly Regex DataRegionTagRegex = new(
        @"(<[a-zA-Z][^>]*?\sdata-sb-data-region(?=[ \t\r\n=/>])(?:=""[^""]*"")?[^>]*?)(/?>)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static IQueryCollection ExtractWidgetParams(HttpContext context, string widgetId)
    {
        var prefix = widgetId + ".";
        Dictionary<string, StringValues>? dict = null;
        foreach (var kvp in context.Request.Query)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                dict ??= new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
                dict[kvp.Key[prefix.Length..]] = kvp.Value;
            }
        }
        return dict is { Count: > 0 } ? new QueryCollection(dict) : context.Request.Query;
    }

    internal static string ComputeWidgetCacheKey(string widgetId, IQueryCollection q)
    {
        var sorted = q
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"{k.Key}={k.Value}");
        return $"sb:widget:{widgetId}:{string.Join("&", sorted)}";
    }

    internal static int QueryPage(IQueryCollection q) =>
        int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

    internal static int QueryPageSize(IQueryCollection q, int defaultSize, int max = int.MaxValue) =>
        int.TryParse(q["pageSize"].FirstOrDefault(), out var ps) && ps > 0 ? Math.Min(ps, max) : defaultSize;

    internal static string InjectOobAttribute(string html)
    {
        // Preferred path: a [data-sb-data-region] element exists in the chunk.
        // Inject hx-swap-oob="innerHTML" on it so HTMX replaces ONLY the contents
        // of the data region, leaving the widget chrome untouched. This is the
        // structural fix for the "flickery resetting" SignalR refresh.
        var regionMatch = DataRegionTagRegex.Match(html);
        if (regionMatch.Success)
        {
            if (regionMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal))
                return html;

            return html[..(regionMatch.Index + regionMatch.Groups[1].Length)]
                   + " hx-swap-oob=\"innerHTML\""
                   + html[(regionMatch.Index + regionMatch.Groups[1].Length)..];
        }

        // Legacy fallback: no data region marked. Inject the old outerHTML OOB on the
        // root. Kept so partials not yet migrated to the two-region contract keep
        // working. The widget will continue to flicker on update -- a deliberate
        // signal that the partial needs migration.
        var rootMatch = FirstTagRegex.Match(html);
        if (!rootMatch.Success) return html;
        if (rootMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;

        return html[..rootMatch.Groups[1].Index]
               + rootMatch.Groups[1].Value
               + " hx-swap-oob=\"true\""
               + rootMatch.Groups[2].Value
               + html[(rootMatch.Index + rootMatch.Length)..];
    }

    /// <summary>
    ///     Collapse rows that share a verified-bot identity name (matcher converged them
    ///     onto one fingerprint, e.g. 26 source IPs all classed as Amazonbot) into a single
    ///     aggregate row. Tool UAs (BotType="Tool"/"Unknown") and synth-named humans stay
    ///     distinct -- they're different actors that just happen to share a UA family.
    ///     Shared by every top-bots build site so the visible row count, pagination, and
    ///     filter results all agree.
    /// </summary>
    public static List<DashboardTopBotEntry> CollapseGroupableIdentities(IReadOnlyList<DashboardTopBotEntry> source)
    {
        var output = new List<DashboardTopBotEntry>();
        foreach (var grp in source.Where(IsGroupableIdentity)
            .GroupBy(b => b.CustomBotName ?? b.BotName, StringComparer.Ordinal))
        {
            var members = grp.ToList();
            if (members.Count == 1) { output.Add(members[0]); continue; }
            var canonical = members.OrderByDescending(b => b.LastSeen).First();
            output.Add(canonical with
            {
                HitCount = members.Sum(b => b.HitCount),
                FirstSeen = members.Min(b => b.FirstSeen == default ? DateTime.MaxValue : b.FirstSeen),
                LastSeen = members.Max(b => b.LastSeen),
                BotProbability = members.Max(b => b.BotProbability)
            });
        }
        output.AddRange(source.Where(b => !IsGroupableIdentity(b)));
        return output.OrderByDescending(b => b.LastSeen).ToList();
    }

    /// <summary>
    ///     Single predicate for "is this row safe to collapse with other same-name rows".
    ///     Used by <see cref="CollapseGroupableIdentities"/> at model-build time, and by the
    ///     SbTopBots view at render time to decide which rows still need a synth-name
    ///     disambiguator (the ones that DON'T get collapsed). Operator-set CustomBotName is
    ///     always trusted; otherwise the UA must have produced a real BotName AND BotType
    ///     must be a known-bot category (not "Tool" / "Unknown") -- tools and humans are
    ///     different actors who share a UA family and must NOT collapse.
    /// </summary>
    public static bool IsGroupableIdentity(DashboardTopBotEntry b) =>
        IsGroupableIdentity(b.CustomBotName, b.BotName, b.BotType);

    /// <summary>
    ///     Primitive-typed predicate overload so non-DashboardTopBotEntry surfaces
    ///     (CachedVisitor in VisitorListCache.GetFiltered, anything else) can share
    ///     the same "is safe to collapse" rule without re-implementing it.
    /// </summary>
    public static bool IsGroupableIdentity(string? customBotName, string? botName, string? botType)
    {
        if (customBotName != null) return true;
        if (botName == null) return false;
        return botType is not null
            && !string.Equals(botType, "Tool", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(botType, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Case-insensitive substring match against the searchable fields of a top-bots row
    ///     (CustomBotName, BotName, BotType, PrimarySignature). Null/empty query returns
    ///     the input unchanged. Shared by every top-bots build site so the search box
    ///     behaves identically across the dashboard middleware, the OOB batch path,
    ///     and the view component.
    /// </summary>
    public static List<DashboardTopBotEntry> ApplySearchFilter(
        IReadOnlyList<DashboardTopBotEntry> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source.ToList();
        var q = query.Trim();
        var cmp = StringComparison.OrdinalIgnoreCase;
        var result = new List<DashboardTopBotEntry>(source.Count);
        foreach (var b in source)
        {
            if ((b.CustomBotName?.Contains(q, cmp) ?? false) ||
                (b.BotName?.Contains(q, cmp) ?? false) ||
                (b.BotType?.Contains(q, cmp) ?? false) ||
                b.PrimarySignature.Contains(q, cmp))
            {
                result.Add(b);
            }
        }
        return result;
    }
}
