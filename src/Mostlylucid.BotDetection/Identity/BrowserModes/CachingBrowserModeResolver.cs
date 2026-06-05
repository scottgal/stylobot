using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     <see cref="IBrowserModeResolver"/> backed by the YAML-driven
///     <see cref="BrowserModeRegistry"/>. First call per request computes the
///     minimum raw-values dict the predicates inspect (method,
///     Sec-Fetch-Pattern, Accept, Accept-Encoding-Ordered,
///     Upgrade-Insecure-Requests) plus an <see cref="IRequestProbe"/> over the
///     HttpContext, walks the registry, and caches the result in
///     <see cref="HttpContext.Items"/>. Subsequent calls return the cached id.
/// </summary>
public sealed class CachingBrowserModeResolver : IBrowserModeResolver
{
    /// <summary>
    ///     HttpContext.Items key. Stable string constant rather than a typed
    ///     marker so other components (dashboard, telemetry) can inspect it
    ///     without taking a hard reference on this assembly.
    /// </summary>
    public const string ItemsKey = "Mostlylucid.BotDetection.BrowserMode";

    private readonly BrowserModeRegistry _registry;

    public CachingBrowserModeResolver(BrowserModeRegistry registry)
    {
        _registry = registry;
    }

    public string Resolve(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemsKey, out var cached) && cached is string s) return s;

        var raw = ComposeMinimalRawValues(context);
        var probe = new HttpContextRequestProbe(context);
        var modeId = _registry.Classify(raw, probe);
        context.Items[ItemsKey] = modeId;
        return modeId;
    }

    /// <summary>
    ///     The minimum subset of raw values the browser-mode YAML predicates
    ///     can reference. Kept narrow on purpose — every key here must be
    ///     computable from the HttpContext alone so the resolver works at
    ///     endpoint-policy time, before any signal-emitting contributor has
    ///     run. Mirrors the encoder field names used by
    ///     <c>IdentityVectorContributor.ComposeRawValues</c> so the YAML
    ///     predicates stay portable between the two call sites.
    /// </summary>
    internal static Dictionary<string, object?> ComposeMinimalRawValues(HttpContext ctx)
    {
        var headers = ctx.Request.Headers;
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["session.method_pattern"] = ctx.Request.Method,
            ["hdr.sec_fetch_pattern"] = SecFetchBitmask(headers),
            ["hdr.upgrade_insecure_requests"] = headers["Upgrade-Insecure-Requests"].ToString() == "1",
            ["hdr.accept"] = headers.Accept.ToString(),
            ["hdr.accept_encoding_ordered"] = headers.AcceptEncoding.ToString(),
        };
    }

    private static int SecFetchBitmask(IHeaderDictionary headers)
    {
        var mask = 0;
        if (headers.ContainsKey("Sec-Fetch-Dest")) mask |= 1;
        if (headers.ContainsKey("Sec-Fetch-Mode")) mask |= 2;
        if (headers.ContainsKey("Sec-Fetch-Site")) mask |= 4;
        if (headers.ContainsKey("Sec-Fetch-User")) mask |= 8;
        return mask;
    }

    private sealed class HttpContextRequestProbe : IRequestProbe
    {
        private readonly HttpContext _ctx;
        public HttpContextRequestProbe(HttpContext ctx) => _ctx = ctx;
        public string Method => _ctx.Request.Method ?? string.Empty;
        public string Path => _ctx.Request.Path.Value ?? string.Empty;
        public bool HasHeader(string name) => _ctx.Request.Headers.ContainsKey(name);
        public string HeaderOrDefault(string name, string fallback)
            => _ctx.Request.Headers.TryGetValue(name, out var v) ? v.ToString() : fallback;
    }
}
