using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Extension methods for integrating bot detection with YARP (Yet Another Reverse Proxy).
/// </summary>
/// <remarks>
///     These extensions provide utilities for adding bot detection headers to proxied requests
///     and implementing bot-aware routing logic.
/// </remarks>
public static class YarpExtensions
{
    /// <summary>
    ///     Adds bot detection result headers to an outgoing request.
    ///     Call this from a YARP request transform to pass bot info to backend services.
    /// </summary>
    /// <param name="httpContext">The current HttpContext with bot detection results</param>
    /// <param name="addHeader">Action to add headers (receives header name and value)</param>
    /// <example>
    ///     <code>
    ///     builder.Services.AddReverseProxy()
    ///         .LoadFromConfig(configuration.GetSection("ReverseProxy"))
    ///         .AddTransforms(context =>
    ///         {
    ///             context.AddRequestTransform(transformContext =>
    ///             {
    ///                 transformContext.HttpContext.AddBotDetectionHeaders(
    ///                     (name, value) => transformContext.ProxyRequest.Headers.TryAddWithoutValidation(name, value));
    ///                 return ValueTask.CompletedTask;
    ///             });
    ///         });
    ///     </code>
    /// </example>
    public static void AddBotDetectionHeaders(this HttpContext httpContext, Action<string, string> addHeader)
    {
        var isBot = httpContext.IsBot();

        // Try to get aggregated evidence for detailed headers
        AggregatedEvidence? evidence = null;
        if (httpContext.Items.TryGetValue(Middleware.BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence ev)
        {
            evidence = ev;
        }

        // Get actual confidence from aggregated evidence if available, otherwise default to 0
        var confidence = evidence?.Confidence ?? 0.0;

        addHeader("X-Bot-Detected", isBot.ToString().ToLowerInvariant());
        addHeader("X-Bot-Confidence", confidence.ToString("F2"));

        if (isBot)
        {
            var botType = httpContext.GetBotType();
            var botName = httpContext.GetBotName();
            var category = httpContext.GetBotCategory();

            if (botType.HasValue)
                addHeader("X-Bot-Type", botType.Value.ToString());

            if (!string.IsNullOrEmpty(botName))
                addHeader("X-Bot-Name", botName);

            if (!string.IsNullOrEmpty(category))
                addHeader("X-Bot-Category", category);

            // Add convenience flags
            addHeader("X-Is-Search-Engine", httpContext.IsSearchEngineBot().ToString().ToLowerInvariant());
            addHeader("X-Is-Malicious-Bot", httpContext.IsMaliciousBot().ToString().ToLowerInvariant());
            addHeader("X-Is-Social-Bot", httpContext.IsSocialMediaBot().ToString().ToLowerInvariant());
        }

        // Always include detection metadata (internal gateway→website headers, not exposed to clients)
        if (evidence != null)
        {
            addHeader("X-Bot-Detection-Probability", evidence.BotProbability.ToString("F4"));
            addHeader("X-Bot-Detection-RiskBand", evidence.RiskBand.ToString());
            addHeader("X-Bot-Detection-ProcessingMs", evidence.TotalProcessingTimeMs.ToString("F2"));

            // Country code from geo enrichment
            if (evidence.Signals != null &&
                evidence.Signals.TryGetValue("geo.country_code", out var ccObj) &&
                ccObj is string cc && cc != "LOCAL")
                addHeader("X-Bot-Detection-Country", cc);

            // Network classification flags for downstream geo/network blocking
            if (evidence.Signals != null)
            {
                var flags = new List<string>(4);
                if (evidence.Signals.TryGetValue(SignalKeys.GeoIsVpn, out var vpn) && vpn is true)
                    flags.Add("vpn");
                if (evidence.Signals.TryGetValue(SignalKeys.GeoIsProxy, out var proxy) && proxy is true)
                    flags.Add("proxy");
                if (evidence.Signals.TryGetValue(SignalKeys.GeoIsTor, out var tor) && tor is true)
                    flags.Add("tor");
                if (evidence.Signals.TryGetValue(SignalKeys.GeoIsHosting, out var hosting) && hosting is true
                    || evidence.Signals.TryGetValue(SignalKeys.IpIsDatacenter, out var dc) && dc is true)
                    flags.Add("datacenter");
                if (flags.Count > 0)
                    addHeader("X-Bot-Detection-NetworkFlags", string.Join(",", flags));
            }

            // Top reasons (JSON array)
            var topReasons = evidence.Contributions
                .Where(c => !string.IsNullOrEmpty(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason)
                .ToList();

            if (topReasons.Any())
                addHeader("X-Bot-Detection-Reasons", JsonSerializer.Serialize(topReasons));

            // Detector contributions (JSON array — internal only, not client-facing)
            var contributionsData = evidence.Contributions
                .GroupBy(c => c.DetectorName)
                .Select(g => new
                {
                    Name = g.Key,
                    g.First().Category,
                    ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                    Weight = g.Sum(c => c.Weight),
                    Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                    Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                    ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                    g.First().Priority
                })
                .OrderByDescending(d => Math.Abs(d.Contribution))
                .ToList();

            if (contributionsData.Any())
                addHeader("X-Bot-Detection-Contributions", JsonSerializer.Serialize(contributionsData));
        }
    }

    /// <summary>
    ///     Adds comprehensive bot detection headers including all detection reasons.
    /// </summary>
    /// <param name="httpContext">The current HttpContext with bot detection results</param>
    /// <param name="addHeader">Action to add headers</param>
    public static void AddBotDetectionHeadersVerbose(this HttpContext httpContext, Action<string, string> addHeader)
    {
        // Add basic headers
        httpContext.AddBotDetectionHeaders(addHeader);

        // Add detection reasons as a semicolon-separated list
        var reasons = httpContext.GetDetectionReasons();
        if (reasons.Count > 0)
        {
            var reasonSummary = string.Join("; ", reasons.Select(r => $"{r.Category}: {r.Detail}"));
            addHeader("X-Bot-Detection-Reasons", reasonSummary);
        }
    }

    /// <summary>
    ///     Adds FULL bot detection headers including all metadata for UI display.
    ///     Extends the basic headers with request metadata, signature ID, and YARP routing info.
    /// </summary>
    /// <param name="httpContext">The current HttpContext with bot detection results</param>
    /// <param name="addHeader">Action to add headers</param>
    public static void AddBotDetectionHeadersFull(this HttpContext httpContext, Action<string, string> addHeader)
    {
        // Add basic headers (includes contributions, processing time, reasons, risk band)
        httpContext.AddBotDetectionHeaders(addHeader);

        // Additional metadata only in full mode
        if (httpContext.Items.TryGetValue(Middleware.BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            addHeader("X-Bot-Detection-Result", evidence.BotProbability > 0.5 ? "true" : "false");
            addHeader("X-Bot-Detection-RequestId", httpContext.TraceIdentifier);

            if (!string.IsNullOrEmpty(evidence.PolicyName))
                addHeader("X-Bot-Detection-Policy", evidence.PolicyName);

            var action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName;
            if (!string.IsNullOrEmpty(action))
                addHeader("X-Bot-Detection-Action", action);
        }

        // Add signature ID if available (for demo/debug mode)
        if (httpContext.Items.TryGetValue("BotDetection.SignatureId", out var signatureId) && signatureId != null)
            addHeader("X-Signature-ID", signatureId.ToString()!);

        // YARP routing info (if available)
        if (httpContext.Items.TryGetValue("Yarp.Cluster", out var cluster) && cluster != null)
            addHeader("X-Bot-Detection-Cluster", cluster.ToString()!);

        if (httpContext.Items.TryGetValue("Yarp.Destination", out var dest) && dest != null)
            addHeader("X-Bot-Detection-Destination", dest.ToString()!);
    }

    /// <summary>
    ///     Determines the YARP cluster to route to based on bot detection results.
    /// </summary>
    /// <param name="httpContext">The current HttpContext</param>
    /// <param name="defaultCluster">Cluster for normal traffic</param>
    /// <param name="crawlerCluster">Optional cluster for search engine bots</param>
    /// <param name="blockCluster">Optional cluster that returns 403 for malicious bots</param>
    /// <returns>The cluster ID to route to</returns>
    public static string GetBotAwareCluster(
        this HttpContext httpContext,
        string defaultCluster,
        string? crawlerCluster = null,
        string? blockCluster = null)
    {
        // Block malicious bots
        if (blockCluster != null && httpContext.IsMaliciousBot())
            return blockCluster;

        // Route search engines to crawler-optimized cluster
        if (crawlerCluster != null && httpContext.IsSearchEngineBot())
            return crawlerCluster;

        return defaultCluster;
    }

    /// <summary>
    ///     Checks if the request should be blocked based on bot detection.
    /// </summary>
    /// <param name="httpContext">The current HttpContext</param>
    /// <param name="minConfidence">Minimum confidence threshold to block</param>
    /// <param name="allowSearchEngines">Whether to allow search engine bots</param>
    /// <param name="allowSocialBots">Whether to allow social media bots</param>
    /// <returns>True if the request should be blocked</returns>
    public static bool ShouldBlockBot(
        this HttpContext httpContext,
        double minConfidence = 0.7,
        bool allowSearchEngines = true,
        bool allowSocialBots = true)
    {
        if (!httpContext.IsBot())
            return false;

        var confidence = httpContext.GetBotConfidence();
        if (confidence < minConfidence)
            return false;

        // Always block malicious bots
        if (httpContext.IsMaliciousBot())
            return true;

        // Check allowed types
        if (allowSearchEngines && httpContext.IsSearchEngineBot())
            return false;

        if (allowSocialBots && httpContext.IsSocialMediaBot())
            return false;

        return true;
    }

    /// <summary>
    ///     Adds TLS/TCP/HTTP2 fingerprinting headers for advanced bot detection.
    ///     Extracts network-layer metadata from the connection and adds as headers
    ///     for downstream analysis by TLS/TCP/HTTP2 fingerprinting contributors.
    /// </summary>
    /// <param name="httpContext">The current HttpContext</param>
    /// <param name="addHeader">Action to add headers</param>
    /// <example>
    ///     <code>
    ///     builder.Services.AddReverseProxy()
    ///         .LoadFromConfig(configuration.GetSection("ReverseProxy"))
    ///         .AddTransforms(context =>
    ///         {
    ///             context.AddRequestTransform(transformContext =>
    ///             {
    ///                 transformContext.HttpContext.AddTlsFingerprintingHeaders(
    ///                     (name, value) => transformContext.ProxyRequest.Headers.TryAddWithoutValidation(name, value));
    ///                 return ValueTask.CompletedTask;
    ///             });
    ///         });
    ///     </code>
    /// </example>
    public static void AddTlsFingerprintingHeaders(this HttpContext httpContext, Action<string, string> addHeader)
    {
        try
        {
            // Extract TLS information
            var tlsFeature = httpContext.Features.Get<ITlsConnectionFeature>();
            if (tlsFeature != null)
                // TLS protocol version (if available via connection info)
                // Note: .NET's ITlsConnectionFeature doesn't expose protocol/cipher directly,
                // but we can get the client certificate
                if (tlsFeature.ClientCertificate != null)
                {
                    addHeader("X-TLS-Client-Cert-Issuer", tlsFeature.ClientCertificate.Issuer);
                    addHeader("X-TLS-Client-Cert-Subject", tlsFeature.ClientCertificate.Subject);
                }

            // HTTP protocol version
            var protocol = httpContext.Request.Protocol;
            if (!string.IsNullOrEmpty(protocol))
            {
                addHeader("X-HTTP-Protocol", protocol);

                // Set HTTP/2 flag if applicable
                if (protocol.StartsWith("HTTP/2", StringComparison.OrdinalIgnoreCase)) addHeader("X-Is-HTTP2", "true");
            }

            // TCP/IP information from connection
            var connection = httpContext.Connection;

            // Client IP (already have this but include for completeness)
            if (connection.RemoteIpAddress != null)
            {
                addHeader("X-Client-IP", connection.RemoteIpAddress.ToString());
                addHeader("X-Client-Port", connection.RemotePort.ToString());
            }

            // Local endpoint info
            if (connection.LocalIpAddress != null)
            {
                addHeader("X-Local-IP", connection.LocalIpAddress.ToString());
                addHeader("X-Local-Port", connection.LocalPort.ToString());
            }

            // Connection ID for tracking
            addHeader("X-Connection-ID", connection.Id);

            // Note: Full TCP/IP stack fingerprinting (TTL, window size, options) requires
            // either packet capture or integration with a reverse proxy that can extract
            // this data (nginx with custom modules, HAProxy, etc.)
            //
            // For full TLS fingerprinting (JA3/JA4), you need:
            // 1. nginx with ssl_ja3 module: https://github.com/fooinha/nginx-ssl-ja3
            // 2. HAProxy with custom Lua scripts
            // 3. Cloudflare Workers (enterprise)
            // 4. Custom packet capture with libpcap/npcap
            //
            // Those would populate these headers:
            // - X-JA3-Hash: MD5 hash of TLS client hello
            // - X-JA3-String: Raw JA3 string
            // - X-TLS-Protocol: TLS version (TLSv1.2, TLSv1.3)
            // - X-TLS-Cipher: Cipher suite name
            // - X-TCP-TTL: Time-to-live value
            // - X-TCP-Window: TCP window size
            // - X-TCP-Options: TCP options string
            // - X-TCP-MSS: Maximum segment size
            // - X-HTTP2-Settings: HTTP/2 SETTINGS frame
        }
        catch (Exception)
        {
            // Silently ignore errors - fingerprinting is best-effort
        }
    }

    /// <summary>
    ///     Adds all bot detection AND fingerprinting headers in one call.
    ///     Combines bot detection results with TLS/TCP/HTTP2 metadata.
    /// </summary>
    /// <param name="httpContext">The current HttpContext</param>
    /// <param name="addHeader">Action to add headers</param>
    public static void AddComprehensiveBotHeaders(this HttpContext httpContext, Action<string, string> addHeader)
    {
        httpContext.AddBotDetectionHeadersFull(addHeader);
        httpContext.AddTlsFingerprintingHeaders(addHeader);
    }
}