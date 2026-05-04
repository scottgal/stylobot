using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

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
        return html[..match.Groups[1].Index]
               + match.Groups[1].Value
               + " hx-swap-oob=\"true\""
               + match.Groups[2].Value
               + html[(match.Index + match.Length)..];
    }
}
