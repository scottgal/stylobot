using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware that broadcasts REAL detection results to the SignalR dashboard hub.
///     Must run AFTER BotDetectionMiddleware to access the detection results.
/// </summary>
public partial class DetectionBroadcastMiddleware
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> CountryCodePropertyCache = new();

    /// <summary>Signal key prefixes allowed through to dashboard (non-PII).</summary>
    private static readonly string[] AllowedSignalPrefixes =
    [
        "ua.", "header.", "client.", "geo.", "ip.", "behavioral.",
        "detection.", "request.", "h2.", "tls.", "tcp.", "h3.",
        "cluster.", "reputation.", "honeypot.", "similarity.",
        "attack.", "ato."
    ];

    /// <summary>Signal keys that must never reach the dashboard.</summary>
    private static readonly HashSet<string> BlockedSignalKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ua.raw", "ip.address", "client_ip", "ip_address",
        "email", "phone", "session_id", "cookie", "authorization"
    };

    /// <summary>Maximum number of signals forwarded to dashboard per detection.</summary>
    private const int MaxSignalsPerDetection = 80;

    /// <summary>Maximum accepted length for X-Bot-Detection-Signals header (16 KB).</summary>
    private const int MaxUpstreamSignalsHeaderLength = 16_384;

    [System.Text.RegularExpressions.GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}")]
    private static partial System.Text.RegularExpressions.Regex EmailPattern();
    private readonly ILogger<DetectionBroadcastMiddleware> _logger;
    private readonly RequestDelegate _next;

    public DetectionBroadcastMiddleware(
        RequestDelegate next,
        ILogger<DetectionBroadcastMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        IOptions<BotDetectionOptions> optionsAccessor,
        VisitorListCache visitorListCache,
        SignatureDescriptionService? signatureDescriptionService = null)
    {
        // Call next middleware first (so detection runs)
        await _next(context);

        // After response, broadcast detection result if available
        try
        {
            // Fast path: upstream-trusted lightweight result (no AggregatedEvidence)
            if (!context.Items.ContainsKey(BotDetectionMiddleware.AggregatedEvidenceKey) &&
                context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var upstreamObj) &&
                upstreamObj is BotDetectionResult upstreamResult)
            {
                await BroadcastUpstreamResult(context, upstreamResult, hubContext, eventStore, visitorListCache);
                return;
            }

            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                evidenceObj is AggregatedEvidence evidence)
            {
                // Skip static assets with zero confidence — these are served by static file
                // middleware with no meaningful detection and pollute reputation history
                if (evidence.Confidence == 0 && evidence.TotalProcessingTimeMs < 0.5)
                {
                    _logger.LogDebug("Skipping broadcast for zero-confidence static asset: {Path}", context.Request.Path);
                    return;
                }

                // Filter out local/private IP detections from broadcast if configured
                var options = optionsAccessor.Value;
                if (options.ExcludeLocalIpFromBroadcast && IsLocalIp(context.Connection.RemoteIpAddress))
                {
                    _logger.LogDebug("Skipping broadcast for local IP {Ip}", context.Connection.RemoteIpAddress);
                    // Still store detection for analysis, just don't broadcast to live feed
                    await StoreDetectionOnly(context, evidence, eventStore);
                    return;
                }
                // Get signature from context
                string? primarySignature = null;
                if (context.Items.TryGetValue("BotDetection.Signatures", out var sigObj) &&
                    sigObj is string sigJson)
                {
                    try
                    {
                        var sigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sigJson);
                        primarySignature = sigs?.GetValueOrDefault("primary");
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to deserialize primary signature JSON");
                    }
                }

                var sigValue = primarySignature ?? GenerateFallbackSignature(context);

                // Build detection event
                var topReasons = evidence.Contributions
                    .Where(c => !string.IsNullOrEmpty(c.Reason))
                    .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                    .Take(5)
                    .Select(c => c.Reason!)
                    .ToList();

                // Extract country code from geo signals if available
                string? countryCode = null;
                if (evidence.Signals != null &&
                    evidence.Signals.TryGetValue("geo.country_code", out var ccObj) &&
                    ccObj is string cc && cc != "LOCAL")
                    countryCode = cc;

                // Fallback: read from GeoRouting middleware context (avoids need for GeoContributor)
                if (countryCode == null &&
                    context.Items.TryGetValue("GeoLocation", out var geoLocObj) &&
                    geoLocObj != null)
                {
                    var countryProp = CountryCodePropertyCache.GetOrAdd(
                        geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
                    if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                        countryCode = geoCC;
                }

                // Fallback: upstream gateway may have resolved geo via X-Country header
                if (countryCode == null)
                    countryCode = ResolveCountryFromHeaders(context);

                // Build detector contributions map for drill-down
                var detectorContributions = evidence.Contributions
                    .GroupBy(c => c.DetectorName)
                    .ToDictionary(
                        g => g.Key,
                        g => new DashboardDetectorContribution
                        {
                            ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                            Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                            Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                            ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                            Priority = g.First().Priority
                        });

                // Build non-PII signals for debugging — allowlist of safe prefixes only
                var importantSignals = new Dictionary<string, object>();
                if (evidence.Signals is { Count: > 0 })
                {
                    importantSignals = evidence.Signals
                        .Where(s => AllowedSignalPrefixes.Any(p => s.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                                    && !BlockedSignalKeys.Contains(s.Key))
                        .Take(MaxSignalsPerDetection)
                        .ToDictionary(s => s.Key, s => s.Value);
                }

                // Always enrich HTTP protocol version — it's non-PII metadata needed for dashboard stats
                EnrichProtocol(context, importantSignals);

                // Enrich with basic request info when signals are sparse (e.g. human fast-path)
                // Only when EnrichHumanSignals is enabled — disabled by default for privacy
                var dashboardOptions = context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()?.Value;
                if (dashboardOptions?.EnrichHumanSignals == true)
                    EnrichFromRequest(context, importantSignals, ref countryCode);

                var detection = new DashboardDetectionEvent
                {
                    RequestId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow,
                    IsBot = evidence.BotProbability > 0.5,
                    BotProbability = evidence.BotProbability,
                    Confidence = evidence.Confidence,
                    RiskBand = evidence.RiskBand.ToString(),
                    BotType = evidence.PrimaryBotType?.ToString(),
                    BotName = evidence.PrimaryBotName,
                    Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName ?? "Allow",
                    PolicyName = evidence.PolicyName ?? "Default",
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value ?? "/",
                    StatusCode = context.Response.StatusCode,
                    ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                    PrimarySignature = sigValue,
                    CountryCode = countryCode,
                    UserAgent = evidence.BotProbability > 0.5 ? SanitizeUserAgent(context.Request.Headers.UserAgent.ToString()) : null,
                    TopReasons = topReasons,
                    DetectorContributions = detectorContributions.Count > 0 ? detectorContributions : null,
                    ImportantSignals = importantSignals
                };

                // Build instant marketing-friendly narrative (no LLM, every request)
                detection = detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };

                // Store detection event in event store
                await eventStore.AddDetectionAsync(detection);

                // Upsert signature ledger (hit_count increments on conflict)
                // Extract individual signature factors if available
                string? ipSig = null, uaSig = null, clientSig = null;
                int factorCount = 1;
                if (context.Items.TryGetValue("BotDetection.Signatures", out var allSigsObj) &&
                    allSigsObj is string allSigsJson)
                {
                    try
                    {
                        var allSigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(allSigsJson);
                        if (allSigs != null)
                        {
                            ipSig = allSigs.GetValueOrDefault("ip");
                            uaSig = allSigs.GetValueOrDefault("ua");
                            clientSig = allSigs.GetValueOrDefault("clientSide");
                            factorCount = allSigs.Count(s => !string.IsNullOrEmpty(s.Value) && s.Key != "primary");
                        }
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to deserialize signature factors JSON");
                    }
                }

                var signature = new DashboardSignatureEvent
                {
                    SignatureId = Guid.NewGuid().ToString("N")[..12],
                    Timestamp = DateTime.UtcNow,
                    PrimarySignature = sigValue,
                    IpSignature = ipSig,
                    UaSignature = uaSig,
                    ClientSideSignature = clientSig,
                    FactorCount = Math.Max(1, factorCount),
                    RiskBand = evidence.RiskBand.ToString(),
                    HitCount = 1, // Will be incremented by DB on conflict
                    IsKnownBot = evidence.BotProbability > 0.5,
                    BotName = evidence.PrimaryBotName,
                    BotProbability = evidence.BotProbability,
                    Confidence = evidence.Confidence,
                    ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                    BotType = evidence.PrimaryBotType?.ToString(),
                    Action = detection.Action,
                    LastPath = detection.Path,
                    Narrative = detection.Narrative,
                    Description = detection.Description,
                    TopReasons = detection.TopReasons.ToList()
                };

                var updatedSignature = await eventStore.AddSignatureAsync(signature);

                // Update server-side visitor cache for HTMX rendering
                var visitor = visitorListCache.Upsert(detection);

                // Broadcast detection and signature to all connected clients
                // Sparkline history is served via API on-demand, not broadcast via SignalR
                await hubContext.Clients.All.BroadcastDetection(detection);
                await hubContext.Clients.All.BroadcastSignature(updatedSignature);

                // Feed signature to SignatureDescriptionService for LLM name/description synthesis
                if (signatureDescriptionService != null && detection.IsBot &&
                    !string.IsNullOrEmpty(sigValue) && evidence.Signals is { Count: > 0 })
                {
                    var nullableSignals = evidence.Signals.ToDictionary(
                        s => s.Key, s => (object?)s.Value);
                    signatureDescriptionService.TrackSignature(sigValue, nullableSignals);
                }

                _logger.LogDebug(
                    "Broadcast detection: {Path} sig={Signature} prob={Probability:F2} hits={HitCount}",
                    detection.Path,
                    detection.PrimarySignature?[..Math.Min(8, detection.PrimarySignature.Length)],
                    detection.BotProbability,
                    updatedSignature.HitCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast detection");
        }
    }

    /// <summary>
    ///     Broadcast a lightweight upstream-trusted detection result.
    ///     Used when TrustUpstreamDetection is enabled and no AggregatedEvidence exists.
    /// </summary>
    private async Task BroadcastUpstreamResult(
        HttpContext context,
        BotDetectionResult result,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        VisitorListCache visitorListCache)
    {
        var options = context.RequestServices.GetService<IOptions<BotDetectionOptions>>()?.Value;
        if (options?.ExcludeLocalIpFromBroadcast == true && IsLocalIp(context.Connection.RemoteIpAddress))
        {
            _logger.LogDebug("Skipping upstream broadcast for local IP {Ip}", context.Connection.RemoteIpAddress);
            return;
        }

        string? primarySignature = null;
        if (context.Items.TryGetValue("BotDetection.Signatures", out var sigObj) && sigObj is string sigJson)
        {
            try
            {
                var sigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sigJson);
                primarySignature = sigs?.GetValueOrDefault("primary");
            }
            catch { /* ignore */ }
        }

        var sigValue = primarySignature ?? GenerateFallbackSignature(context);
        var botProbability = result.ConfidenceScore; // Legacy field: actually holds bot probability
        var riskBand = botProbability switch
        {
            >= 0.85 => "VeryHigh",
            >= 0.7 => "High",
            >= 0.4 => "Medium",
            >= 0.2 => "Low",
            _ => "VeryLow"
        };

        // Get actual detection confidence from AggregatedEvidence if available
        var detectionConfidence = botProbability; // Default: match probability for backward compat
        if (context.Items.TryGetValue(BotDetectionMiddleware.DetectionConfidenceKey, out var confObj) &&
            confObj is double parsedConf)
            detectionConfidence = parsedConf;
        else if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                 evidenceObj is AggregatedEvidence upstreamEvidence)
            detectionConfidence = upstreamEvidence.Confidence;

        // Read gateway processing time from forwarded header
        double upstreamProcessingMs = 0;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-ProcessingMs", out var procHeader))
            double.TryParse(procHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out upstreamProcessingMs);

        // Read upstream reasons
        var topReasons = result.Reasons
            .Where(r => !string.IsNullOrEmpty(r.Detail))
            .Take(5)
            .Select(r => r.Detail!)
            .ToList();

        // Read upstream country code (try multiple header sources and context items)
        var upstreamCountry = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault()
                              ?? ResolveCountryFromHeaders(context);

        // Only show BotType when probability >= 0.5 (consistent with DetectionLedgerExtensions)
        // Prevents "Human Visitor" rows showing "Type: Scraper" at 46% probability
        var botType = result.IsBot ? result.BotType?.ToString() : null;

        // Build ImportantSignals for upstream detections — needed for dashboard widgets
        // (browser, protocol, etc.) since upstream path doesn't run full detector pipeline
        var importantSignals = new Dictionary<string, object>();

        // Read forwarded signals from upstream gateway headers if available
        var upstreamSignalsHeader = context.Request.Headers["X-Bot-Detection-Signals"].FirstOrDefault();
        if (!string.IsNullOrEmpty(upstreamSignalsHeader) && upstreamSignalsHeader.Length <= MaxUpstreamSignalsHeaderLength)
        {
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(upstreamSignalsHeader);
                if (parsed != null)
                {
                    var count = 0;
                    foreach (var kvp in parsed)
                    {
                        if (count >= MaxSignalsPerDetection) break;
                        if (BlockedSignalKeys.Contains(kvp.Key)) continue;
                        if (!AllowedSignalPrefixes.Any(p => kvp.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                        // Convert JsonElement to appropriate CLR type
                        object value = kvp.Value.ValueKind switch
                        {
                            System.Text.Json.JsonValueKind.String => kvp.Value.GetString()!,
                            System.Text.Json.JsonValueKind.Number => kvp.Value.TryGetInt64(out var l) ? l : kvp.Value.GetDouble(),
                            System.Text.Json.JsonValueKind.True => true,
                            System.Text.Json.JsonValueKind.False => false,
                            _ => kvp.Value.ToString()
                        };
                        importantSignals[kvp.Key] = value;
                        count++;
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to deserialize upstream signals header");
            }
        }

        // Always enrich protocol version from request metadata
        EnrichProtocol(context, importantSignals);

        // Enrich browser info when EnrichHumanSignals is enabled
        var dashboardOptions = context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()?.Value;
        if (dashboardOptions?.EnrichHumanSignals == true)
            EnrichFromRequest(context, importantSignals, ref upstreamCountry);

        var detection = new DashboardDetectionEvent
        {
            RequestId = context.TraceIdentifier,
            Timestamp = DateTime.UtcNow,
            IsBot = result.IsBot,
            BotProbability = botProbability,
            Confidence = detectionConfidence,
            RiskBand = riskBand,
            BotType = botType,
            BotName = result.IsBot ? result.BotName : null,
            Action = context.Request.Headers["X-Bot-Detection-Action"].FirstOrDefault() ?? "Allow",
            PolicyName = "upstream",
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? "/",
            StatusCode = context.Response.StatusCode,
            ProcessingTimeMs = upstreamProcessingMs,
            PrimarySignature = sigValue,
            CountryCode = upstreamCountry,
            UserAgent = result.IsBot ? SanitizeUserAgent(context.Request.Headers.UserAgent.ToString()) : null,
            TopReasons = topReasons,
            ImportantSignals = importantSignals
        };

        detection = detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };

        await eventStore.AddDetectionAsync(detection);
        visitorListCache.Upsert(detection);
        // Sparkline history served via API, not broadcast via SignalR
        await hubContext.Clients.All.BroadcastDetection(detection);

        _logger.LogDebug(
            "Broadcast upstream detection: {Path} sig={Signature} prob={Probability:F2}",
            detection.Path,
            sigValue[..Math.Min(8, sigValue.Length)],
            detection.BotProbability);
    }

    /// <summary>
    ///     Check if an IP address is a local/private network address.
    ///     Supports both IPv4 and IPv6.
    /// </summary>
    internal static bool IsLocalIp(IPAddress? ip)
    {
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        // IPv6 link-local (fe80::/10)
        if (ip.IsIPv6LinkLocal) return true;

        // IPv6 site-local (fec0::/10 — deprecated but still used)
        if (ip.IsIPv6SiteLocal) return true;

        // IPv6 unique local address (fc00::/7 — ULA, equivalent to RFC 1918)
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            // fc00::/7 means first byte is 0xFC or 0xFD
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        // Map IPv6-mapped IPv4 to IPv4 for private range checks
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // IPv4 private ranges
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                    // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31,       // 172.16.0.0/12
                192 => bytes[1] == 168,                        // 192.168.0.0/16
                169 => bytes[1] == 254,                        // 169.254.0.0/16 (link-local)
                127 => true,                                   // 127.0.0.0/8
                _ => false
            };
        }

        return false;
    }

    /// <summary>
    ///     Store a detection event without broadcasting to SignalR (for local IP filtering).
    /// </summary>
    private async Task StoreDetectionOnly(HttpContext context, AggregatedEvidence evidence, IDashboardEventStore eventStore)
    {
        try
        {
            string? primarySignature = null;
            if (context.Items.TryGetValue("BotDetection.Signatures", out var sigObj) &&
                sigObj is string sigJson)
            {
                try
                {
                    var sigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sigJson);
                    primarySignature = sigs?.GetValueOrDefault("primary");
                }
                catch { }
            }

            var topReasons = evidence.Contributions
                .Where(c => !string.IsNullOrEmpty(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason!)
                .ToList();

            var detection = new DashboardDetectionEvent
            {
                RequestId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow,
                IsBot = evidence.BotProbability > 0.5,
                BotProbability = evidence.BotProbability,
                Confidence = evidence.Confidence,
                RiskBand = evidence.RiskBand.ToString(),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName ?? "Allow",
                Method = context.Request.Method,
                Path = context.Request.Path.Value ?? "/",
                StatusCode = context.Response.StatusCode,
                ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                PrimarySignature = primarySignature ?? GenerateFallbackSignature(context),
                TopReasons = topReasons,
                Narrative = DetectionNarrativeBuilder.Build(new DashboardDetectionEvent
                {
                    RequestId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow,
                    IsBot = evidence.BotProbability > 0.5,
                    BotProbability = evidence.BotProbability,
                    Confidence = evidence.Confidence,
                    RiskBand = evidence.RiskBand.ToString(),
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value ?? "/",
                    TopReasons = topReasons
                })
            };

            await eventStore.AddDetectionAsync(detection);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to store local detection");
        }
    }

    /// <summary>
    ///     Sanitize a User-Agent string for broadcast to dashboard clients.
    ///     Strips email addresses that some crawlers embed (e.g. "MyBot/1.0 (+mailto:admin@example.com)").
    ///     UA strings themselves are public HTTP headers, not PII — only embedded emails need redacting.
    /// </summary>
    /// <summary>
    ///     Resolve country code from upstream headers and HttpContext items.
    ///     Checks X-Country, CF-IPCountry (Cloudflare), and GeoDetection.CountryCode context item.
    /// </summary>
    private static string? ResolveCountryFromHeaders(HttpContext context)
    {
        // Common upstream gateway headers
        var country = context.Request.Headers["X-Country"].FirstOrDefault()
                      ?? context.Request.Headers["CF-IPCountry"].FirstOrDefault();

        if (!string.IsNullOrEmpty(country) && country != "XX" && country != "LOCAL")
            return country;

        // GeoDetection middleware context item (set by Mostlylucid.GeoDetection)
        if (context.Items.TryGetValue("GeoDetection.CountryCode", out var geoCtx) &&
            geoCtx is string geoCountry && !string.IsNullOrEmpty(geoCountry) && geoCountry != "LOCAL")
            return geoCountry;

        return null;
    }

    private static string? SanitizeUserAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        // Strip email addresses that some crawlers embed (e.g. "MyBot/1.0 (+mailto:admin@example.com)")
        return EmailPattern().Replace(ua, "[email-redacted]");
    }

    /// <summary>
    ///     Generate a fallback signature if the real one isn't available.
    /// </summary>
    private string GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var combined = $"{ip}:{ua}";

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    ///     Always enrich protocol version — non-PII metadata the dashboard needs for protocol stats.
    ///     Sets h3.protocol, h2.protocol signals when not already present from fingerprint detectors.
    /// </summary>
    private static void EnrichProtocol(HttpContext context, Dictionary<string, object> signals)
    {
        if (signals.ContainsKey("h3.protocol") || signals.ContainsKey("h3.version") ||
            signals.ContainsKey("h2.fingerprint") || signals.ContainsKey("h2.settings_hash") ||
            signals.ContainsKey("h2.protocol"))
            return; // Already enriched by fingerprint detectors

        var protocol = context.Request.Protocol;
        if (protocol.Contains("3", StringComparison.Ordinal))
            signals.TryAdd("h3.protocol", "h3");
        else if (protocol.Contains("2", StringComparison.Ordinal))
            signals.TryAdd("h2.protocol", "h2");
        // HTTP/1.1 is the fallback — dashboard infers it when no h2/h3 signal present
    }

    /// <summary>
    ///     Enrich ImportantSignals with basic non-PII request metadata when signals are sparse.
    ///     Extracts browser family, major version, and country code from HTTP headers.
    ///     Protocol enrichment is handled by <see cref="EnrichProtocol"/> which runs unconditionally.
    ///     Only called when <see cref="StyloBotDashboardOptions.EnrichHumanSignals"/> is true.
    /// </summary>
    private static void EnrichFromRequest(HttpContext context, Dictionary<string, object> signals, ref string? countryCode)
    {
        // Browser family + version from User-Agent (non-PII: browser name is not identifying)
        if (!signals.ContainsKey("ua.browser") && !signals.ContainsKey("ua.family"))
        {
            var ua = context.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(ua))
            {
                var (browser, version) = ParseBrowserFromUa(ua);
                if (browser != null)
                {
                    signals.TryAdd("ua.browser", browser);
                    if (version != null)
                        signals.TryAdd("ua.browser_version", version);
                }
            }
        }

        // Country code from GeoLocation context (set by GeoDetection middleware)
        if (countryCode == null &&
            context.Items.TryGetValue("GeoLocation", out var geoLocObj) && geoLocObj != null)
        {
            var countryProp = CountryCodePropertyCache.GetOrAdd(
                geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
            if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                countryCode = geoCC;
        }

        // Fallback: upstream gateway headers (X-Country, CF-IPCountry, etc.)
        if (countryCode == null)
            countryCode = ResolveCountryFromHeaders(context);
    }

    /// <summary>
    ///     Lightweight UA parser — extracts browser family and major version.
    ///     Not a full parser; handles Chrome, Firefox, Safari, Edge, Opera.
    /// </summary>
    private static (string? Browser, string? Version) ParseBrowserFromUa(string ua)
    {
        // Order matters: Edge before Chrome (Edge contains "Chrome")
        ReadOnlySpan<char> uaSpan = ua.AsSpan();

        if (ua.Contains("Edg/", StringComparison.Ordinal))
            return ("Edge", ExtractVersion(uaSpan, "Edg/"));
        if (ua.Contains("OPR/", StringComparison.Ordinal))
            return ("Opera", ExtractVersion(uaSpan, "OPR/"));
        if (ua.Contains("Firefox/", StringComparison.Ordinal))
            return ("Firefox", ExtractVersion(uaSpan, "Firefox/"));
        if (ua.Contains("Chrome/", StringComparison.Ordinal) && !ua.Contains("Chromium", StringComparison.Ordinal))
            return ("Chrome", ExtractVersion(uaSpan, "Chrome/"));
        if (ua.Contains("Safari/", StringComparison.Ordinal) && ua.Contains("Version/", StringComparison.Ordinal))
            return ("Safari", ExtractVersion(uaSpan, "Version/"));
        if (ua.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
            ua.Contains("spider", StringComparison.OrdinalIgnoreCase))
            return (null, null); // Don't enrich bot UAs — they already have signals

        return (null, null);
    }

    private static string? ExtractVersion(ReadOnlySpan<char> ua, string token)
    {
        var idx = ua.IndexOf(token.AsSpan(), StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + token.Length;
        var end = start;
        while (end < ua.Length && (char.IsDigit(ua[end]) || ua[end] == '.'))
            end++;
        if (end == start) return null;
        var full = ua[start..end].ToString();
        // Return major version only (e.g. "131" from "131.0.6778.86")
        var dot = full.IndexOf('.');
        return dot > 0 ? full[..dot] : full;
    }
}
