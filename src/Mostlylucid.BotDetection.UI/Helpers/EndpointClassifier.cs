namespace Mostlylucid.BotDetection.UI.Helpers;

/// <summary>
///     Who serves an endpoint: the upstream application the gateway reverse-
///     proxies to, or StyloBot's own dashboard / admin / API surface.
/// </summary>
public enum EndpointOwner
{
    Upstream,
    Stylobot,
}

/// <summary>
///     MODE bucket for the endpoints list filter (Si2 endpoint-IA unification).
///     Purely a path/method shape classification -- <see cref="Models.DashboardEndpointStats"/>
///     is aggregated by (method, path) and carries no per-request transport signal
///     (the composite browser-mode taxonomy -- navigation/xhr/sub-resource/
///     signalr-negotiate/websocket-upgrade -- lives on fingerprints/sessions via
///     IdentityBrowserMode, not on endpoint aggregates), so SignalR and raw
///     WebSocket endpoints are not distinguishable at this read layer and both
///     collapse into <see cref="Realtime"/>.
/// </summary>
public enum EndpointMode
{
    /// <summary>Human page view: GET/HEAD, upstream, non-API, non-static.</summary>
    Content,
    /// <summary>API/machine endpoint (see <see cref="EndpointClassifier.IsApiPath"/>).</summary>
    Api,
    /// <summary>Static asset by extension.</summary>
    Static,
    /// <summary>SignalR hub / negotiate / WebSocket upgrade path.</summary>
    Realtime,
    /// <summary>Everything else (e.g. non-GET form submits against a non-API path).</summary>
    Other,
}

/// <summary>
///     Read-time classification of dashboard endpoint rows by owner (StyloBot vs
///     upstream) and kind (content page vs API vs static asset).
///     <para>
///         Used by <c>SbEndpointsList</c> to (a) preselect "content pages" —
///         upstream, non-API, non-static — for the traffic overview, and (b)
///         badge each row's owner so operators can tell their own app's pages
///         apart from StyloBot's injected UI.
///     </para>
///     <para>
///         The ownership rule mirrors <c>StyloBotDashboardMiddleware</c>'s own
///         self-path test: StyloBot owns everything under the configured
///         dashboard <c>BasePath</c> (plus the framework static-asset / SignalR
///         roots the UI package injects at the host root); everything else falls
///         through to the upstream app. Path-based on purpose — it needs no
///         gateway or store change (the boundary IS the path prefix), so it ships
///         with the dashboard UI alone.
///     </para>
/// </summary>
public static class EndpointClassifier
{
    // The dashboard UI package always mounts at "/stylobot" regardless of the
    // configured BasePath (see FossDashboardGroups: "every mount — /dashboard,
    // /stylobot, /_stylobot"), and the SignalR hub defaults to /stylobot/hub.
    // The "/_"-prefixed roots (/_stylobot, /_content, /_blazor, /_framework) are
    // caught by the framework-convention rule in Classify, so only "/stylobot"
    // needs listing here.
    private static readonly string[] StylobotMountPrefixes = ["/stylobot"];

    // Same extension set the endpoints view component used before this helper
    // centralised it — kept here so "is a static asset" has one owner.
    private static readonly string[] StaticExtensions =
        [".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".woff", ".woff2", ".ttf", ".map"];

    /// <summary>True when the path ends in a known static-asset extension.</summary>
    public static bool IsStaticResource(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var ext in StaticExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    ///     True when the path looks like an API / machine endpoint rather than a
    ///     human-facing page: an <c>/api/</c> segment, a version-prefixed route
    ///     (<c>/v1/…</c>, <c>/v2/…</c> — e.g. OTLP <c>/v1/logs</c>), or a gRPC /
    ///     protobuf service path (a dotted service segment like
    ///     <c>/opentelemetry.proto.collector…</c>).
    /// </summary>
    public static bool IsApiPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/api/", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/api", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
               || IsVersionApiPrefix(path)
               || IsGrpcServicePath(path);
    }

    // First path segment is a version tag: /v1/, /v2/, ... Catches OTLP ingest
    // (/v1/logs, /v1/traces) and versioned REST without matching words like
    // "/verify" (the segment must be 'v' followed only by digits).
    private static bool IsVersionApiPrefix(string path)
    {
        if (path.Length < 4 || path[0] != '/' || (path[1] != 'v' && path[1] != 'V')) return false;
        var i = 2;
        while (i < path.Length && char.IsDigit(path[i])) i++;
        return i > 2 && i < path.Length && path[i] == '/';
    }

    // A dotted service segment, e.g. gRPC/protobuf "/opentelemetry.proto.collector.
    // logs.v1.LogsService/Export" — the first segment contains dots, which a
    // human-facing page path never does.
    private static bool IsGrpcServicePath(string path)
    {
        if (path.Length < 2 || path[0] != '/') return false;
        var end = path.IndexOf('/', 1);
        var firstSegment = end < 0 ? path[1..] : path[1..end];
        return firstSegment.Contains('.');
    }

    /// <summary>
    ///     Classify a path's owner. StyloBot owns everything under
    ///     <paramref name="basePath"/> (the dashboard mount) and the framework
    ///     asset roots; everything else is upstream.
    /// </summary>
    public static EndpointOwner Classify(string? path, string? basePath, string? navBasePath = null)
    {
        if (string.IsNullOrEmpty(path)) return EndpointOwner.Upstream;

        // ASP.NET framework / RCL convention: a first path segment beginning with
        // '_' is framework-reserved (/_stylobot partials, /_content RCL assets,
        // /_blazor, /_framework). A human content page never starts with "/_".
        if (path.StartsWith("/_", StringComparison.Ordinal)) return EndpointOwner.Stylobot;

        // Configured dashboard mount(s) — page routes + nav.
        if (IsUnder(path, basePath) || IsUnder(path, navBasePath)) return EndpointOwner.Stylobot;

        // Always-on package mounts (/stylobot + the /stylobot/hub SignalR endpoint),
        // present regardless of the configured BasePath.
        foreach (var pre in StylobotMountPrefixes)
            if (IsUnder(path, pre)) return EndpointOwner.Stylobot;

        return EndpointOwner.Upstream;
    }

    // Segment-boundary prefix match: "/x" matches "/x" and "/x/…" but not "/xyz".
    private static bool IsUnder(string? path, string? prefix)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefix)) return false;
        var bp = "/" + prefix.Trim('/');
        if (bp.Length <= 1) return false;
        return path.Equals(bp, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(bp + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True for a "content page": served by the upstream app, not an API
    ///     endpoint, and not a static asset. This is the set the traffic
    ///     overview's "Top content pages" widget shows.
    /// </summary>
    public static bool IsUpstreamContent(string? path, string? basePath, string? navBasePath = null)
        => !string.IsNullOrEmpty(path)
           && Classify(path, basePath, navBasePath) == EndpointOwner.Upstream
           && !IsApiPath(path)
           && !IsStaticResource(path);

    /// <summary>
    ///     True for a "content page" ROW on the traffic overview: a human page
    ///     view. Adds the method gate on top of <see cref="IsUpstreamContent"/> —
    ///     a page view is a <c>GET</c> (or <c>HEAD</c>); <c>POST</c>/<c>PUT</c>/…
    ///     are form submits, API calls, or telemetry ingest (e.g. OTLP
    ///     <c>POST /v1/logs</c>), which are not "pages".
    /// </summary>
    public static bool IsContentPageRequest(string? method, string? path, string? basePath, string? navBasePath = null)
        => (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
           && IsUpstreamContent(path, basePath, navBasePath);

    /// <summary>
    ///     Classify a (method, path) endpoint-list row into an <see cref="EndpointMode"/>
    ///     bucket for the MODE filter chip. Order matters: static/realtime are checked
    ///     ahead of API/content because a hub path or an asset extension can otherwise
    ///     also look API- or version-prefixed shaped.
    /// </summary>
    public static EndpointMode ClassifyMode(string? method, string? path)
    {
        if (string.IsNullOrEmpty(path)) return EndpointMode.Other;
        if (IsStaticResource(path)) return EndpointMode.Static;
        if (IsRealtimePath(path)) return EndpointMode.Realtime;
        if (IsApiPath(path)) return EndpointMode.Api;
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
            return EndpointMode.Content;
        return EndpointMode.Other;
    }

    // SignalR's own hub route ("/hub", "/stylobot/hub", …) and its "/negotiate"
    // sub-route are the one path convention ASP.NET Core SignalR guarantees
    // regardless of which transport (WebSockets, SSE, long-polling) the client
    // ultimately negotiates -- so matching on "hub"/"negotiate" catches SignalR
    // traffic without needing the live transport. A raw (non-SignalR) WebSocket
    // endpoint has no such convention; "/ws" and "/websocket" are the common
    // host-app naming patterns this heuristic also catches.
    private static bool IsRealtimePath(string path) =>
        path.Contains("/hub", StringComparison.OrdinalIgnoreCase)
        || path.Contains("negotiate", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/websocket", StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowercase URL-filter token for an <see cref="EndpointMode"/> (the <c>?mode=</c> value).</summary>
    public static string ModeToken(EndpointMode mode) => mode switch
    {
        EndpointMode.Content => "content",
        EndpointMode.Api => "api",
        EndpointMode.Static => "static",
        EndpointMode.Realtime => "realtime",
        _ => "other",
    };
}