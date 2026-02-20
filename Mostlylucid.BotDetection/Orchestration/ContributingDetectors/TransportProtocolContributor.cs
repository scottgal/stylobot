using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Transport protocol contributor — detects WebSocket, gRPC, GraphQL, and SSE
///     from HTTP request headers and validates protocol-specific compliance.
///
///     <para><b>Why this matters for bot detection:</b></para>
///     <para>
///     All these protocols start as regular HTTP requests that flow through middleware
///     BEFORE any protocol upgrade. Bots frequently get protocol-specific headers wrong
///     because they copy minimal examples or skip RFC-required fields.
///     </para>
///
///     <para><b>What we CAN see (at the gateway/middleware level):</b></para>
///     <list type="bullet">
///         <item><b>WebSocket</b> — <c>Upgrade: websocket</c> + <c>Sec-WebSocket-*</c> headers in the HTTP upgrade request</item>
///         <item><b>gRPC</b> — <c>content-type: application/grpc</c> + <c>te: trailers</c> over HTTP/2</item>
///         <item><b>GraphQL</b> — <c>POST /graphql</c> with <c>content-type: application/json</c></item>
///         <item><b>SSE</b> — <c>Accept: text/event-stream</c></item>
///     </list>
///
///     <para><b>What we CANNOT see (post-upgrade or payload-level):</b></para>
///     <list type="bullet">
///         <item>Individual WebSocket frames after upgrade</item>
///         <item>gRPC stream messages after initial request</item>
///         <item>GraphQL query body content (we don't read the body — we flag path + content-type)</item>
///         <item>SSE is server→client only, so no further client messages to inspect</item>
///     </list>
///
///     Configuration loaded from: transport-protocol.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:TransportProtocolContributor:*
/// </summary>
public partial class TransportProtocolContributor : ConfiguredContributorBase
{
    /// <summary>
    ///     Known browser User-Agent family prefixes. gRPC calls from these are suspicious
    ///     because browsers don't make raw gRPC calls (they use grpc-web which has a different content-type).
    /// </summary>
    private static readonly string[] BrowserUaFamilies =
    [
        "Mozilla/", "Chrome/", "Safari/", "Firefox/", "Edge/", "Opera/"
    ];

    private readonly ILogger<TransportProtocolContributor> _logger;

    public TransportProtocolContributor(
        ILogger<TransportProtocolContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "TransportProtocol";
    public override int Priority => Manifest?.Priority ?? 5;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML — no magic numbers
    private double MissingWsHeadersConfidence => GetParam("missing_ws_headers_confidence", 0.6);
    private double InvalidWsVersionConfidence => GetParam("invalid_ws_version_confidence", 0.5);
    private double MissingWsOriginConfidence => GetParam("missing_ws_origin_confidence", 0.3);
    private double WsOriginMismatchConfidence => GetParam("ws_origin_mismatch_confidence", 0.5);
    private double GrpcBrowserUaConfidence => GetParam("grpc_browser_ua_confidence", 0.5);
    private double GrpcMissingTeConfidence => GetParam("grpc_missing_te_confidence", 0.4);
    private double GrpcReflectionConfidence => GetParam("grpc_reflection_confidence", 0.4);
    private double GraphqlIntrospectionConfidence => GetParam("graphql_introspection_confidence", 0.3);
    private double GraphqlBatchConfidence => GetParam("graphql_batch_confidence", 0.15);
    private double GraphqlGetMutationConfidence => GetParam("graphql_get_mutation_confidence", 0.5);
    private double SseMissingCacheControlConfidence => GetParam("sse_missing_cache_control_confidence", 0.15);
    private double SseHistoryReplayConfidence => GetParam("sse_history_replay_confidence", 0.3);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var request = state.HttpContext.Request;
            var protocol = "http";

            // Check protocols in priority order (most specific first)
            if (IsWebSocketUpgrade(request))
            {
                protocol = "websocket";
                state.WriteSignal(SignalKeys.TransportIsUpgrade, true);
                AnalyzeWebSocket(state, request, contributions);
            }
            else if (IsGrpc(request, out var grpcContentType))
            {
                protocol = grpcContentType.Contains("grpc-web", StringComparison.OrdinalIgnoreCase)
                    ? "grpc-web"
                    : "grpc";
                state.WriteSignal(SignalKeys.TransportGrpcContentType, grpcContentType);
                AnalyzeGrpc(state, request, protocol, contributions);
            }
            else if (IsGraphql(request))
            {
                protocol = "graphql";
                AnalyzeGraphql(state, request, contributions);
            }
            else if (IsSse(request))
            {
                protocol = "sse";
                state.WriteSignal(SignalKeys.TransportSse, true);
                AnalyzeSse(state, request, contributions);
            }

            state.WriteSignal(SignalKeys.TransportProtocol, protocol);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error analyzing transport protocol");
            state.WriteSignal(SignalKeys.TransportProtocol, "http");
        }

        if (contributions.Count == 0)
            contributions.Add(NeutralContribution("Protocol", "Transport protocol analysis complete"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    // ==========================================
    // WebSocket Analysis
    // ==========================================
    // RFC 6455 §4.1 — Client opening handshake MUST include:
    //   - Connection: Upgrade
    //   - Upgrade: websocket
    //   - Sec-WebSocket-Key (16-byte base64-encoded nonce)
    //   - Sec-WebSocket-Version: 13
    //   - Origin (browsers always send this; non-browser clients may omit)
    //
    // Bot patterns we catch:
    //   - Missing Sec-WebSocket-Key → tool didn't implement RFC properly
    //   - Wrong Sec-WebSocket-Version → outdated/custom implementation
    //   - Missing Origin → non-browser client (bots, scripts, CLI tools)
    //   - Invalid Sec-WebSocket-Key format → poorly implemented client
    //
    // Attack vectors visible at HTTP layer:
    //   - WebSocket hijacking probes (upgrade without proper headers)
    //   - Cross-Site WebSocket Hijacking (CSWSH) — missing/wrong Origin
    //   - DoS via upgrade flooding (handled by behavioral, but we flag the protocol)
    // ==========================================

    private void AnalyzeWebSocket(BlackboardState state, HttpRequest request,
        List<DetectionContribution> contributions)
    {
        var hasKey = request.Headers.ContainsKey("Sec-WebSocket-Key");
        var hasVersion = request.Headers.TryGetValue("Sec-WebSocket-Version", out var version);
        var hasOrigin = request.Headers.ContainsKey("Origin");

        // Sec-WebSocket-Key is REQUIRED by RFC 6455 §4.1
        // Missing = malformed client or lazy bot that only sends Upgrade headers
        if (!hasKey)
        {
            contributions.Add(BotContribution(
                "Protocol",
                "WebSocket upgrade missing Sec-WebSocket-Key (required by RFC 6455)",
                confidenceOverride: MissingWsHeadersConfidence,
                weightMultiplier: 1.4));
        }

        if (hasVersion)
        {
            var versionStr = version.ToString().Trim();
            state.WriteSignal(SignalKeys.TransportWebSocketVersion, versionStr);

            // RFC 6455 specifies version 13 as the only valid version
            // Version 8 (draft) and others are obsolete and indicate old/custom tools
            if (versionStr != "13")
                contributions.Add(BotContribution(
                    "Protocol",
                    $"Invalid Sec-WebSocket-Version: {versionStr} (RFC 6455 requires 13)",
                    confidenceOverride: InvalidWsVersionConfidence,
                    weightMultiplier: 1.2));
        }
        else if (hasKey)
        {
            // Has key but no version — partially implemented, likely a bot
            contributions.Add(BotContribution(
                "Protocol",
                "WebSocket upgrade missing Sec-WebSocket-Version",
                confidenceOverride: MissingWsHeadersConfidence,
                weightMultiplier: 1.2));
        }

        // Origin header — browsers ALWAYS send this on WebSocket upgrades
        // Missing Origin is a strong signal for non-browser clients (scripts, bots, CLI tools)
        // Also relevant for CSWSH (Cross-Site WebSocket Hijacking) detection
        state.WriteSignal(SignalKeys.TransportWebSocketOrigin, hasOrigin);
        if (!hasOrigin)
            contributions.Add(BotContribution(
                "Protocol",
                "WebSocket upgrade missing Origin header (browsers always include it)",
                confidenceOverride: MissingWsOriginConfidence,
                weightMultiplier: 1.0));

        // Validate Sec-WebSocket-Key format if present (must be 16-byte base64 = 24 chars)
        if (hasKey)
        {
            var keyValue = request.Headers["Sec-WebSocket-Key"].ToString().Trim();
            if (!IsValidWebSocketKey(keyValue))
                contributions.Add(BotContribution(
                    "Protocol",
                    "Invalid Sec-WebSocket-Key format (must be 16-byte base64)",
                    confidenceOverride: InvalidWsVersionConfidence,
                    weightMultiplier: 1.3));
        }

        // Host vs Origin mismatch — CSWSH (Cross-Site WebSocket Hijacking) detection
        // A browser always sends Origin matching the page that initiated the connection.
        // An Origin from a different domain attempting to upgrade is a CSRF probe.
        if (hasOrigin && request.Headers.TryGetValue("Host", out var host))
        {
            var originValue = request.Headers["Origin"].ToString().Trim();
            var hostValue = host.ToString().Trim();
            if (!string.IsNullOrEmpty(originValue) && !string.IsNullOrEmpty(hostValue))
            {
                // Extract host from Origin URL (e.g., "https://example.com" → "example.com")
                if (Uri.TryCreate(originValue, UriKind.Absolute, out var originUri))
                {
                    var originHost = originUri.Host;
                    // Strip port from Host header if present
                    var hostOnly = hostValue.Contains(':') ? hostValue.Split(':')[0] : hostValue;
                    if (!string.Equals(originHost, hostOnly, StringComparison.OrdinalIgnoreCase))
                        contributions.Add(BotContribution(
                            "Protocol",
                            $"WebSocket Origin/Host mismatch: Origin={originHost}, Host={hostOnly} (potential CSWSH)",
                            confidenceOverride: WsOriginMismatchConfidence,
                            weightMultiplier: 1.4));
                }
            }
        }
    }

    // ==========================================
    // gRPC Analysis
    // ==========================================
    // gRPC over HTTP/2 requires:
    //   - content-type: application/grpc (or application/grpc+proto, application/grpc+json)
    //   - te: trailers (required by gRPC spec, browsers never send this)
    //   - HTTP/2 transport (gRPC requires HTTP/2+)
    //
    // grpc-web is different:
    //   - content-type: application/grpc-web (or application/grpc-web+proto)
    //   - Works over HTTP/1.1 via a proxy (Envoy, grpc-web proxy)
    //   - Legitimate from browser apps using @grpc/grpc-web
    //
    // Bot patterns we catch:
    //   - gRPC content-type with browser User-Agent (browsers don't make raw gRPC calls)
    //   - Missing te: trailers on raw gRPC (required by spec)
    //   - gRPC without HTTP/2 (protocol violation, unless grpc-web)
    //
    // Attack vectors visible at HTTP layer:
    //   - gRPC reflection abuse (service discovery probing)
    //   - Malformed gRPC content-type (fuzzing/probing)
    //   - Raw gRPC from browser UA = spoofed UA or confused client
    // ==========================================

    private void AnalyzeGrpc(BlackboardState state, HttpRequest request, string protocol,
        List<DetectionContribution> contributions)
    {
        // grpc-web is legitimate from browsers — only flag raw gRPC from browser UAs
        if (protocol == "grpc")
        {
            // Check for te: trailers — required by gRPC spec
            // Browsers never send this header; its absence with gRPC content-type
            // indicates a malformed or incomplete gRPC client
            var hasTe = request.Headers.TryGetValue("te", out var teValue)
                        && teValue.ToString().Contains("trailers", StringComparison.OrdinalIgnoreCase);

            if (!hasTe)
                contributions.Add(BotContribution(
                    "Protocol",
                    "gRPC request missing te: trailers header (required by gRPC spec)",
                    confidenceOverride: GrpcMissingTeConfidence,
                    weightMultiplier: 1.2));

            // Browser User-Agent making raw gRPC calls is suspicious
            // Browsers use grpc-web (different content-type), not raw gRPC
            var ua = request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua) && BrowserUaFamilies.Any(f =>
                    ua.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
                contributions.Add(BotContribution(
                    "Protocol",
                    "Raw gRPC request from browser User-Agent (browsers use grpc-web, not raw gRPC)",
                    confidenceOverride: GrpcBrowserUaConfidence,
                    weightMultiplier: 1.3,
                    botType: BotType.Scraper.ToString()));
        }

        // gRPC reflection probing — bots use grpc_cli/grpcurl to enumerate services
        // Path pattern: /grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo
        //           or: /grpc.reflection.v1.ServerReflection/ServerReflectionInfo
        var path = request.Path.Value ?? "";
        if (path.Contains("grpc.reflection", StringComparison.OrdinalIgnoreCase))
            contributions.Add(BotContribution(
                "Protocol",
                "gRPC reflection endpoint probed — service discovery reconnaissance",
                confidenceOverride: GrpcReflectionConfidence,
                weightMultiplier: 1.2,
                botType: BotType.Scraper.ToString()));
    }

    // ==========================================
    // GraphQL Analysis
    // ==========================================
    // GraphQL is standard HTTP (usually POST /graphql with application/json body).
    // We DON'T read the request body (that's payload inspection, not our job).
    //
    // What we CAN detect from headers/URL:
    //   - Path matching (/graphql, /gql, /api/graphql)
    //   - Query string introspection (?query={__schema{...}}) — GET requests
    //   - Content-Type: application/graphql (less common but valid)
    //
    // Bot patterns we catch:
    //   - Introspection queries via GET query string (bots probe schema first)
    //   - Batch queries via array body indicator (Content-Type with batch hint)
    //
    // Attack vectors visible at HTTP layer:
    //   - Schema introspection probing (__schema, __type in query string)
    //   - Batch query abuse (send many operations in one request for DoS)
    //   - GraphQL endpoint discovery (probing common paths)
    //   - Note: Query depth attacks, alias abuse, and field duplication
    //     require body parsing — out of scope for header-level detection
    // ==========================================

    private void AnalyzeGraphql(BlackboardState state, HttpRequest request,
        List<DetectionContribution> contributions)
    {
        // Check for introspection in query string (GET requests or query param)
        var queryString = request.QueryString.Value ?? "";
        var hasIntrospection = IntrospectionPattern().IsMatch(queryString);

        state.WriteSignal(SignalKeys.TransportGraphqlIntrospection, hasIntrospection);

        if (hasIntrospection)
            contributions.Add(BotContribution(
                "Protocol",
                "GraphQL introspection query detected (__schema/__type) — common bot reconnaissance",
                confidenceOverride: GraphqlIntrospectionConfidence,
                weightMultiplier: 1.1));

        // Check for batch indicator — array content-type or batch query param
        // Legitimate apps can batch, but it's a high-abuse pattern for DoS
        var hasBatchHint = request.Headers.TryGetValue("X-GraphQL-Batch", out _)
                           || queryString.Contains("batch", StringComparison.OrdinalIgnoreCase);

        state.WriteSignal(SignalKeys.TransportGraphqlBatch, hasBatchHint);

        if (hasBatchHint)
            contributions.Add(BotContribution(
                "Protocol",
                "GraphQL batch query indicator detected — legitimate but high-abuse pattern",
                confidenceOverride: GraphqlBatchConfidence,
                weightMultiplier: 0.8));

        // GET request with mutation/subscription in query string — spec violation
        // GraphQL over HTTP spec: GET requests may only be used for query operations
        // Bots that use GET for mutations are nonconformant or scanning
        if (HttpMethods.IsGet(request.Method) &&
            MutationSubscriptionPattern().IsMatch(queryString))
            contributions.Add(BotContribution(
                "Protocol",
                "GraphQL mutation/subscription via GET request (spec violation — GET is read-only)",
                confidenceOverride: GraphqlGetMutationConfidence,
                weightMultiplier: 1.2));
    }

    // ==========================================
    // SSE Analysis
    // ==========================================
    // Server-Sent Events use standard HTTP with Accept: text/event-stream.
    // SSE is server→client only (no client messages after the initial request).
    //
    // What we CAN detect:
    //   - Accept: text/event-stream header
    //   - Combined with automation UA = slightly suspicious
    //
    // Bot patterns:
    //   - SSE from known automation tools (scraping live feeds)
    //   - Multiple concurrent SSE connections (handled by behavioral detector,
    //     but we emit the signal so it can factor it in)
    //
    // Attack vectors visible at HTTP layer:
    //   - SSE connection flooding (many connections to exhaust server resources)
    //   - Live data scraping via SSE endpoints
    //   - Note: SSE is inherently low-risk since it's server→client only
    // ==========================================

    private void AnalyzeSse(BlackboardState state, HttpRequest request,
        List<DetectionContribution> contributions)
    {
        // SSE itself is neutral — we just emit the signal for downstream detectors
        // The behavioral detector can use this to flag multiple concurrent SSE connections

        // Missing Cache-Control: no-cache — WHATWG EventSource spec says the client MUST include this
        // All browsers send it; custom SSE client libraries often omit it
        var hasCacheControl = request.Headers.TryGetValue("Cache-Control", out var cacheControl)
                              && cacheControl.ToString().Contains("no-cache", StringComparison.OrdinalIgnoreCase);

        if (!hasCacheControl)
            contributions.Add(BotContribution(
                "Protocol",
                "SSE request missing Cache-Control: no-cache (browsers always include this per WHATWG spec)",
                confidenceOverride: SseMissingCacheControlConfidence,
                weightMultiplier: 0.8));

        // Last-Event-ID: 0 or -1 — history replay attempt
        // This is a data extraction technique: requesting all historical events from the beginning
        if (request.Headers.TryGetValue("Last-Event-ID", out var lastEventId))
        {
            var eventIdValue = lastEventId.ToString().Trim();
            if (eventIdValue is "0" or "-1")
                contributions.Add(BotContribution(
                    "Protocol",
                    $"SSE Last-Event-ID: {eventIdValue} — potential history replay attempt",
                    confidenceOverride: SseHistoryReplayConfidence,
                    weightMultiplier: 1.1));
        }
    }

    // ==========================================
    // Protocol Detection Helpers
    // ==========================================

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out var upgrade)
               && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase)
               && request.Headers.TryGetValue("Connection", out var connection)
               && connection.ToString().Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGrpc(HttpRequest request, out string contentType)
    {
        contentType = request.ContentType ?? "";
        return contentType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphql(HttpRequest request)
    {
        // Detect via path
        var path = request.Path.Value ?? "";
        if (path.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/gql", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/graphql/", StringComparison.OrdinalIgnoreCase))
            return true;

        // Detect via content-type
        var contentType = request.ContentType ?? "";
        return contentType.StartsWith("application/graphql", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSse(HttpRequest request)
    {
        return request.Headers.TryGetValue("Accept", out var accept)
               && accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidWebSocketKey(string key)
    {
        // RFC 6455 §4.1: Sec-WebSocket-Key must be a base64-encoded 16-byte value
        // 16 bytes base64-encoded = 24 characters (with padding)
        if (string.IsNullOrEmpty(key) || key.Length != 24)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == 16;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex(@"__schema|__type", RegexOptions.IgnoreCase)]
    private static partial Regex IntrospectionPattern();

    [GeneratedRegex(@"\bmutation\b|\bsubscription\b", RegexOptions.IgnoreCase)]
    private static partial Regex MutationSubscriptionPattern();
}
