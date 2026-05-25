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
        // Inject hx-swap-oob="morph:innerHTML" on it so Idiomorph (the official
        // htmx morph extension, loaded via SbLiveUpdates) mutates the data
        // region's children in place -- unchanged rows are left untouched,
        // only the deltas mutate. Rows the operator's cursor is over, links
        // they're about to click, and any focused / hovered DOM nodes survive
        // a beacon. This replaces the previous innerHTML wholesale-replace
        // which tore down and rebuilt every row on every signal.
        var regionMatch = DataRegionTagRegex.Match(html);
        if (regionMatch.Success)
        {
            if (regionMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal))
                return html;

            return html[..(regionMatch.Index + regionMatch.Groups[1].Length)]
                   + " hx-swap-oob=\"morph:innerHTML\""
                   + html[(regionMatch.Index + regionMatch.Groups[1].Length)..];
        }

        // Fallback: no data region marked. Morph the whole widget element in
        // place. Same morph algorithm, applied to the root rather than its
        // children -- unchanged subtrees still go untouched, no flicker.
        var rootMatch = FirstTagRegex.Match(html);
        if (!rootMatch.Success) return html;
        if (rootMatch.Value.Contains("hx-swap-oob", StringComparison.Ordinal)) return html;

        return html[..rootMatch.Groups[1].Index]
               + rootMatch.Groups[1].Value
               + " hx-swap-oob=\"morph:outerHTML\""
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
        // Single pass to preserve the caller's sort order. Each output item carries
        // the position of its earliest source occurrence so a final OrderBy returns
        // results in caller-supplied order; the previous version always re-sorted
        // by LastSeen DESC which silently overrode any sort the caller passed in
        // (sort=name, sort=threat, sort=hits etc. all rendered as lastseen).
        var firstIndexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupMembers = new Dictionary<string, List<DashboardTopBotEntry>>(StringComparer.Ordinal);
        var passThrough = new List<(int idx, DashboardTopBotEntry entry)>();

        for (var i = 0; i < source.Count; i++)
        {
            var b = source[i];
            if (IsGroupableIdentity(b))
            {
                var key = b.CustomBotName ?? b.BotName ?? string.Empty;
                if (!firstIndexOf.ContainsKey(key))
                {
                    firstIndexOf[key] = i;
                    groupMembers[key] = new List<DashboardTopBotEntry>();
                }
                groupMembers[key].Add(b);
            }
            else
            {
                passThrough.Add((i, b));
            }
        }

        var collapsed = new List<(int idx, DashboardTopBotEntry entry)>(groupMembers.Count);
        foreach (var (key, members) in groupMembers)
        {
            var minIdx = firstIndexOf[key];
            if (members.Count == 1)
            {
                collapsed.Add((minIdx, members[0]));
                continue;
            }
            var canonical = members.OrderByDescending(b => b.LastSeen).First();
            collapsed.Add((minIdx, canonical with
            {
                HitCount = members.Sum(b => b.HitCount),
                FirstSeen = members.Min(b => b.FirstSeen == default ? DateTime.MaxValue : b.FirstSeen),
                LastSeen = members.Max(b => b.LastSeen),
                BotProbability = members.Max(b => b.BotProbability)
            }));
        }

        return collapsed.Concat(passThrough)
            .OrderBy(x => x.idx)
            .Select(x => x.entry)
            .ToList();
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
