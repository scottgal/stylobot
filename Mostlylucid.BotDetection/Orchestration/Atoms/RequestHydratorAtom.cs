using Microsoft.AspNetCore.Http;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Hydrator atom that extracts signals from HttpContext and populates SignalSink.
///     This is the entry point for all request data - detectors read from signals, not HttpContext.
/// </summary>
/// <remarks>
///     **Flow:**
///     ```
///     HttpContext → RequestHydratorAtom → SignalSink → Other Detector Atoms
///     ```
///     The hydrator runs first (Priority = 0) and emits canonical signals that all other detectors consume.
///     This decouples detectors from HttpContext, making them testable and portable.
/// </remarks>
public sealed class RequestHydratorAtom : DetectorAtomBase
{
    public RequestHydratorAtom() : base("RequestHydrator", "Infrastructure")
    {
    }

    public override int Priority => 0; // Always runs first
    public override bool IsOptional => false; // Must succeed

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        await Task.CompletedTask; // Sync operation but interface is async

        // The HttpContext is passed via session context (explained below)
        // For now, emit an info contribution indicating hydration occurred
        return Single(DetectionContribution.Info(
            Name,
            Category,
            "Request signals hydrated to sink"));
    }

    /// <summary>
    ///     Hydrates the SignalSink from an HttpContext.
    ///     Call this before running detection to populate all request signals.
    /// </summary>
    public static void HydrateFromContext(SignalSink sink, HttpContext context, string sessionId)
    {
        var request = context.Request;
        var connection = context.Connection;

        // === Request Basics ===
        sink.Raise($"request.method:{request.Method}", sessionId);
        sink.Raise($"request.path:{request.Path}", sessionId);
        sink.Raise($"request.scheme:{request.Scheme}", sessionId);
        if (request.QueryString.HasValue)
            sink.Raise("request.has_query", sessionId);

        // === Headers ===
        var headerCount = request.Headers.Count;
        sink.Raise($"request.header_count:{headerCount}", sessionId);

        // Standard headers (presence signals)
        if (request.Headers.ContainsKey("User-Agent"))
            sink.Raise("header.user_agent.present", sessionId);
        if (request.Headers.ContainsKey("Accept"))
            sink.Raise("header.accept.present", sessionId);
        if (request.Headers.ContainsKey("Accept-Language"))
            sink.Raise("header.accept_language.present", sessionId);
        if (request.Headers.ContainsKey("Accept-Encoding"))
            sink.Raise("header.accept_encoding.present", sessionId);
        if (request.Headers.ContainsKey("Referer"))
            sink.Raise("header.referer.present", sessionId);
        if (request.Headers.ContainsKey("Cookie"))
            sink.Raise("header.cookie.present", sessionId);
        if (request.Headers.ContainsKey("DNT"))
            sink.Raise("header.dnt.present", sessionId);
        if (request.Headers.ContainsKey("Upgrade-Insecure-Requests"))
            sink.Raise("header.upgrade_insecure.present", sessionId);

        // Security headers
        if (request.Headers.ContainsKey("Sec-Fetch-Site"))
            sink.Raise("header.sec_fetch.present", sessionId);
        if (request.Headers.ContainsKey("Sec-CH-UA"))
            sink.Raise("header.client_hints.present", sessionId);

        // === User-Agent Classification (without storing raw UA) ===
        var userAgent = request.Headers.UserAgent.ToString();
        if (!string.IsNullOrEmpty(userAgent))
        {
            sink.Raise($"ua.length:{userAgent.Length}", sessionId);

            // Common bot indicators
            if (userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase))
                sink.Raise("ua.contains_bot_keyword", sessionId);

            if (userAgent.Contains("curl", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("wget", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("httpie", StringComparison.OrdinalIgnoreCase))
                sink.Raise("ua.is_cli_tool", sessionId);

            if (userAgent.Contains("python", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("requests", StringComparison.OrdinalIgnoreCase) ||
                userAgent.Contains("axios", StringComparison.OrdinalIgnoreCase))
                sink.Raise("ua.is_http_library", sessionId);

            // Browser detection
            if (userAgent.Contains("Chrome/"))
                sink.Raise("ua.browser:chrome", sessionId);
            else if (userAgent.Contains("Firefox/"))
                sink.Raise("ua.browser:firefox", sessionId);
            else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome"))
                sink.Raise("ua.browser:safari", sessionId);
            else if (userAgent.Contains("Edg/"))
                sink.Raise("ua.browser:edge", sessionId);

            // OS detection
            if (userAgent.Contains("Windows"))
                sink.Raise("ua.os:windows", sessionId);
            else if (userAgent.Contains("Mac OS") || userAgent.Contains("Macintosh"))
                sink.Raise("ua.os:macos", sessionId);
            else if (userAgent.Contains("Linux"))
                sink.Raise("ua.os:linux", sessionId);
            else if (userAgent.Contains("Android"))
                sink.Raise("ua.os:android", sessionId);
            else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad"))
                sink.Raise("ua.os:ios", sessionId);
        }
        else
        {
            sink.Raise("ua.empty", sessionId);
        }

        // === Connection Info ===
        var ip = connection.RemoteIpAddress;
        if (ip != null)
        {
            sink.Raise("ip.present", sessionId);

            // IPv4 vs IPv6
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                sink.Raise("ip.type:ipv6", sessionId);
            else
                sink.Raise("ip.type:ipv4", sessionId);

            // Local/Loopback detection
            if (System.Net.IPAddress.IsLoopback(ip))
                sink.Raise("ip.is_loopback", sessionId);

            // Private network detection
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168))
                    sink.Raise("ip.is_private", sessionId);
            }
        }
        else
        {
            sink.Raise("ip.missing", sessionId);
        }

        // === Protocol ===
        sink.Raise($"protocol:{request.Protocol}", sessionId);
        if (request.IsHttps)
            sink.Raise("protocol.is_https", sessionId);

        // === Timing ===
        sink.Raise($"request.timestamp:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", sessionId);

        // Signal that hydration is complete
        sink.Raise("hydration.complete", sessionId);
    }
}
