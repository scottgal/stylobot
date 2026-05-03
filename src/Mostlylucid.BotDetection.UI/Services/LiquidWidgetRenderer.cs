using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Fluid;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed class LiquidWidgetRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = new() { MaxSteps = 50_000 };
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new();
    private readonly ILogger<LiquidWidgetRenderer> _logger;
    private const int MaxCacheSize = 500;

    public LiquidWidgetRenderer(ILogger<LiquidWidgetRenderer> logger)
    {
        _logger = logger;
        Options.MemberAccessStrategy.Register<Dictionary<string, object>, object?>(
            (dict, name) => dict.TryGetValue(name, out var v) ? v : null);
    }

    public async Task<string?> RenderAsync(
        string widgetId, string template, Dictionary<string, object> context)
    {
        try
        {
            if (_cache.Count >= MaxCacheSize)
            {
                // Evict one arbitrary entry to avoid thundering-herd cost of a full-cache clear.
                var keyToEvict = _cache.Keys.FirstOrDefault();
                if (keyToEvict != null) _cache.TryRemove(keyToEvict, out _);
            }

            var compiled = _cache.GetOrAdd(ComputeKey(widgetId, template), _ =>
            {
                if (!Parser.TryParse(template, out var t, out var errors))
                    throw new InvalidOperationException(
                        $"Liquid parse error in '{widgetId}': {string.Join("; ", errors)}");
                return t;
            });

            var ctx = new TemplateContext(Options);
            foreach (var (key, value) in context)
                ctx.SetValue(key, value);

            var sb = new StringBuilder();
            using var tw = new StringWriter(sb);
            await compiled.RenderAsync(tw, HtmlEncoder.Default, ctx);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "LiquidWidgetRenderer: failed to render widget '{Widget}'", widgetId);
            return null;
        }
    }

    private static string ComputeKey(string widgetId, string template)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(template));
        return $"{widgetId}:{Convert.ToHexString(hash)[..16]}";
    }
}