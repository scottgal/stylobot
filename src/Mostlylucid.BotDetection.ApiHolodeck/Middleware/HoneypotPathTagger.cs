using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.ApiHolodeck.Middleware;

/// <summary>
///     Pre-detection middleware that tags honeypot paths on HttpContext.Items.
///     Runs before BotDetectionMiddleware so the tag is available even when
///     early exit prevents the HoneypotLinkContributor from running.
/// </summary>
public sealed class HoneypotPathTagger
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _exactPaths;
    private readonly List<string> _prefixPaths;

    public HoneypotPathTagger(RequestDelegate next, IOptions<HolodeckOptions> options)
    {
        _next = next;
        var paths = options.Value.HoneypotPaths;
        _exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _prefixPaths = new List<string>();

        foreach (var p in paths)
        {
            var normalized = p.TrimEnd('/');
            _exactPaths.Add(normalized);
            _prefixPaths.Add(normalized);
        }
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (_exactPaths.Contains(path.TrimEnd('/')))
        {
            context.Items["Holodeck.IsHoneypotPath"] = true;
            context.Items["Holodeck.MatchedPath"] = path;
            return _next(context);
        }

        foreach (var prefix in _prefixPaths)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (path.Length == prefix.Length || path[prefix.Length] == '/'))
            {
                context.Items["Holodeck.IsHoneypotPath"] = true;
                context.Items["Holodeck.MatchedPath"] = prefix;
                return _next(context);
            }
        }

        return _next(context);
    }
}
