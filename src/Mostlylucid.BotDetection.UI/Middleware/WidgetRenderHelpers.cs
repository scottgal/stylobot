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
        var match = FirstTagRegex.Match(html);
        if (!match.Success) return html;
        if (match.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;
        // OOB swap value is "true" -- HTMX 2.0's OOB parser does NOT accept the
        // "outerHTML transition:true" syntax (verified live: that value zeroed
        // out oobBeforeSwap events entirely). View Transitions are now applied
        // client-side by SbLiveUpdatesTagHelper wrapping htmx.ajax in
        // document.startViewTransition, which works for the whole OOB batch
        // including elements whose OOB attribute is just "true".
        return html[..match.Groups[1].Index]
               + match.Groups[1].Value
               + " hx-swap-oob=\"true\""
               + match.Groups[2].Value
               + html[(match.Index + match.Length)..];
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
}
