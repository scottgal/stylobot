using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware that broadcasts REAL detection results to the SignalR dashboard hub.
///     Must run AFTER BotDetectionMiddleware to access the detection results.
/// </summary>
public class DetectionBroadcastMiddleware
{
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
        VisitorListCache visitorListCache)
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

                // Build non-PII signals for debugging
                Dictionary<string, object>? importantSignals = null;
                if (evidence.Signals is { Count: > 0 })
                {
                    var allowedPrefixes = new[] { "ua.", "header.", "client.", "geo.", "ip.", "behavioral.", "detection.", "request.", "h2.", "tls.", "tcp." };
                    var blockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "client_ip", "ip_address", "email", "phone", "session_id", "cookie", "authorization" };

                    importantSignals = evidence.Signals
                        .Where(s => allowedPrefixes.Any(p => s.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                                    && !blockedKeys.Contains(s.Key))
                        .Take(50)
                        .ToDictionary(s => s.Key, s => s.Value);
                }

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
                    UserAgent = evidence.BotProbability > 0.5 ? context.Request.Headers.UserAgent.ToString() : null,
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
                    IsKnownBot = evidence.PrimaryBotType.HasValue,
                    BotName = evidence.PrimaryBotName
                };

                var updatedSignature = await eventStore.AddSignatureAsync(signature);

                // Update server-side visitor cache for HTMX rendering
                visitorListCache.Upsert(detection);

                // Broadcast detection and signature (with hit_count) to all connected clients
                await hubContext.Clients.All.BroadcastDetection(detection);
                await hubContext.Clients.All.BroadcastSignature(updatedSignature);

                // LLM description generation is now handled by LlmClassificationCoordinator (background)

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

        // Read upstream country code
        var upstreamCountry = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault();

        // Only show BotType when probability >= 0.5 (consistent with DetectionLedgerExtensions)
        // Prevents "Human Visitor" rows showing "Type: Scraper" at 46% probability
        var botType = result.IsBot ? result.BotType?.ToString() : null;

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
            UserAgent = result.IsBot ? context.Request.Headers.UserAgent.ToString() : null,
            TopReasons = topReasons
        };

        detection = detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };

        await eventStore.AddDetectionAsync(detection);
        visitorListCache.Upsert(detection);
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
    ///     Generate a fallback signature if the real one isn't available.
    /// </summary>
    private string GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var combined = $"{ip}:{ua}";

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
