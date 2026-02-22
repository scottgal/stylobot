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
        "attack.", "ato.", "intent."
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
        SignatureAggregateCache signatureAggregateCache,
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
                var options = optionsAccessor.Value;
                if (options.ExcludeLocalIpFromBroadcast && IsLocalIp(context.Connection.RemoteIpAddress))
                {
                    _logger.LogDebug("Skipping upstream broadcast for local IP {Ip}", context.Connection.RemoteIpAddress);
                    return;
                }

                var detection = BuildDetectionFromUpstream(context, upstreamResult);
                var updatedSignature = await StoreDetectionAndSignatureAsync(context, detection, eventStore);

                signatureAggregateCache.UpdateFromDetection(detection);
                visitorListCache.Upsert(detection);
                await hubContext.Clients.All.BroadcastDetection(detection);
                await hubContext.Clients.All.BroadcastSignature(updatedSignature);

                _logger.LogDebug(
                    "Broadcast upstream detection: {Path} sig={Signature} prob={Probability:F2} hits={HitCount}",
                    detection.Path,
                    detection.PrimarySignature?[..Math.Min(8, detection.PrimarySignature.Length)],
                    detection.BotProbability,
                    updatedSignature.HitCount);
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

                var detection = BuildDetectionFromEvidence(context, evidence);
                var updatedSignature = await StoreDetectionAndSignatureAsync(context, detection, eventStore);

                signatureAggregateCache.UpdateFromDetection(detection);

                // Filter out local/private IP detections from broadcast if configured
                var options = optionsAccessor.Value;
                if (options.ExcludeLocalIpFromBroadcast && IsLocalIp(context.Connection.RemoteIpAddress))
                {
                    _logger.LogDebug("Skipping broadcast for local IP {Ip}", context.Connection.RemoteIpAddress);
                    return; // Stored to DB above, just don't broadcast to live feed
                }

                // Update server-side visitor cache and broadcast
                visitorListCache.Upsert(detection);
                await hubContext.Clients.All.BroadcastDetection(detection);
                await hubContext.Clients.All.BroadcastSignature(updatedSignature);

                // Feed signature to SignatureDescriptionService for LLM name/description synthesis
                if (signatureDescriptionService != null && detection.IsBot &&
                    !string.IsNullOrEmpty(detection.PrimarySignature) && evidence.Signals is { Count: > 0 })
                {
                    var nullableSignals = evidence.Signals.ToDictionary(
                        s => s.Key, s => (object?)s.Value);
                    signatureDescriptionService.TrackSignature(detection.PrimarySignature, nullableSignals);
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

    // ─── Shared storage: ONE path for detection + signature ───────────────

    /// <summary>
    ///     Single method that stores both detection and signature to the event store.
    ///     Every code path calls this — no duplication.
    /// </summary>
    private async Task<DashboardSignatureEvent> StoreDetectionAndSignatureAsync(
        HttpContext context,
        DashboardDetectionEvent detection,
        IDashboardEventStore eventStore)
    {
        await eventStore.AddDetectionAsync(detection);

        var factors = ParseSignatureFactors(context);
        var signature = new DashboardSignatureEvent
        {
            SignatureId = Guid.NewGuid().ToString("N")[..12],
            Timestamp = DateTime.UtcNow,
            PrimarySignature = detection.PrimarySignature,
            IpSignature = factors.IpSig,
            UaSignature = factors.UaSig,
            ClientSideSignature = factors.ClientSig,
            FactorCount = factors.FactorCount,
            RiskBand = detection.RiskBand,
            HitCount = 1, // DB increments on conflict
            IsKnownBot = detection.IsBot,
            BotName = detection.BotName,
            BotProbability = detection.BotProbability,
            Confidence = detection.Confidence,
            ProcessingTimeMs = detection.ProcessingTimeMs,
            BotType = detection.BotType,
            Action = detection.Action,
            LastPath = detection.Path,
            Narrative = detection.Narrative,
            Description = detection.Description,
            TopReasons = detection.TopReasons?.ToList(),
            ThreatScore = detection.ThreatScore,
            ThreatBand = detection.ThreatBand,
        };

        return await eventStore.AddSignatureAsync(signature);
    }

    // ─── Detection builders ──────────────────────────────────────────────

    /// <summary>
    ///     Build a DashboardDetectionEvent from full AggregatedEvidence (local detection path).
    /// </summary>
    private DashboardDetectionEvent BuildDetectionFromEvidence(HttpContext context, AggregatedEvidence evidence)
    {
        var sigValue = ResolvePrimarySignature(context);
        var countryCode = ResolveCountryCode(context, evidence.Signals);

        var topReasons = evidence.Contributions
            .Where(c => !string.IsNullOrEmpty(c.Reason))
            .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
            .Take(5)
            .Select(c => c.Reason!)
            .ToList();

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

        var importantSignals = BuildImportantSignals(context, evidence.Signals, ref countryCode);

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
            ImportantSignals = importantSignals,
            ThreatScore = evidence.ThreatScore > 0 ? evidence.ThreatScore : null,
            ThreatBand = evidence.ThreatBand != Orchestration.ThreatBand.None
                ? evidence.ThreatBand.ToString() : null,
        };

        return detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };
    }

    /// <summary>
    ///     Build a DashboardDetectionEvent from upstream-trusted BotDetectionResult (no AggregatedEvidence).
    /// </summary>
    private DashboardDetectionEvent BuildDetectionFromUpstream(HttpContext context, BotDetectionResult result)
    {
        var sigValue = ResolvePrimarySignature(context);
        var botProbability = result.ConfidenceScore; // Legacy field: actually holds bot probability
        var riskBand = botProbability switch
        {
            >= 0.85 => "VeryHigh",
            >= 0.7 => "High",
            >= 0.4 => "Medium",
            >= 0.2 => "Low",
            _ => "VeryLow"
        };

        var detectionConfidence = botProbability;
        Dictionary<string, DashboardDetectorContribution>? detectorContributions = null;

        if (context.Items.TryGetValue(BotDetectionMiddleware.DetectionConfidenceKey, out var confObj) &&
            confObj is double parsedConf)
            detectionConfidence = parsedConf;
        else if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                 evidenceObj is AggregatedEvidence upstreamEvidence)
        {
            detectionConfidence = upstreamEvidence.Confidence;

            // Extract detector contributions from evidence when available
            var contributions = upstreamEvidence.Contributions
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
            if (contributions.Count > 0)
                detectorContributions = contributions;
        }

        double upstreamProcessingMs = 0;
        if (context.Request.Headers.TryGetValue("X-Bot-Detection-ProcessingMs", out var procHeader))
            double.TryParse(procHeader.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out upstreamProcessingMs);

        var topReasons = result.Reasons
            .Where(r => !string.IsNullOrEmpty(r.Detail))
            .Take(5)
            .Select(r => r.Detail!)
            .ToList();

        var upstreamCountry = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault()
                              ?? ResolveCountryFromHeaders(context);

        var botType = result.IsBot ? result.BotType?.ToString() : null;

        var importantSignals = ParseUpstreamSignals(context);
        EnrichProtocol(context, importantSignals);

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
            DetectorContributions = detectorContributions,
            ImportantSignals = importantSignals,
            ThreatScore = importantSignals.TryGetValue("intent.threat_score", out var tsObj)
                && tsObj is double tsVal ? tsVal : null,
            ThreatBand = importantSignals.TryGetValue("intent.threat_band", out var tbObj)
                ? tbObj?.ToString() : null,
        };

        return detection with { Narrative = DetectionNarrativeBuilder.Build(detection) };
    }

    // ─── Shared helpers (used by all paths) ──────────────────────────────

    /// <summary>
    ///     Parse signature factors from HttpContext.Items — ONE place, not three.
    /// </summary>
    private record SignatureFactors(string? IpSig, string? UaSig, string? ClientSig, int FactorCount);

    private static SignatureFactors ParseSignatureFactors(HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetection.Signatures", out var allSigsObj))
        {
            // Preferred path: MultiFactorSignatures object
            if (allSigsObj is Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures mfs)
            {
                var factorCount = 0;
                if (!string.IsNullOrEmpty(mfs.IpSignature)) factorCount++;
                if (!string.IsNullOrEmpty(mfs.UaSignature)) factorCount++;
                if (!string.IsNullOrEmpty(mfs.ClientSideSignature)) factorCount++;
                if (!string.IsNullOrEmpty(mfs.PluginSignature)) factorCount++;
                if (!string.IsNullOrEmpty(mfs.IpSubnetSignature)) factorCount++;
                if (!string.IsNullOrEmpty(mfs.GeoSignature)) factorCount++;
                return new SignatureFactors(
                    mfs.IpSignature,
                    mfs.UaSignature,
                    mfs.ClientSideSignature,
                    Math.Max(1, factorCount));
            }

            // Legacy path: JSON string
            if (allSigsObj is string allSigsJson)
            {
                try
                {
                    var allSigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(allSigsJson);
                    if (allSigs != null)
                    {
                        return new SignatureFactors(
                            allSigs.GetValueOrDefault("ip"),
                            allSigs.GetValueOrDefault("ua"),
                            allSigs.GetValueOrDefault("clientSide"),
                            Math.Max(1, allSigs.Count(s => !string.IsNullOrEmpty(s.Value) && s.Key != "primary")));
                    }
                }
                catch (System.Text.Json.JsonException) { }
            }
        }

        return new SignatureFactors(null, null, null, 1);
    }

    /// <summary>
    ///     Resolve the primary signature from HttpContext, falling back to hash-based generation.
    /// </summary>
    private string ResolvePrimarySignature(HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetection.Signatures", out var sigObj))
        {
            // Preferred path: MultiFactorSignatures object from ComputeAndStoreSignature
            if (sigObj is Mostlylucid.BotDetection.Dashboard.MultiFactorSignatures mfs &&
                !string.IsNullOrEmpty(mfs.PrimarySignature))
                return mfs.PrimarySignature;

            // Legacy path: JSON string (from upstream gateway headers)
            if (sigObj is string sigJson)
            {
                try
                {
                    var sigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sigJson);
                    var primary = sigs?.GetValueOrDefault("primary");
                    if (!string.IsNullOrEmpty(primary)) return primary;
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to deserialize primary signature JSON");
                }
            }
        }

        return GenerateFallbackSignature(context);
    }

    /// <summary>
    ///     Resolve country code from evidence signals, GeoLocation context, and headers.
    /// </summary>
    private static string? ResolveCountryCode(HttpContext context, IReadOnlyDictionary<string, object>? signals)
    {
        // From detection signals
        if (signals != null &&
            signals.TryGetValue("geo.country_code", out var ccObj) &&
            ccObj is string cc && cc != "LOCAL")
            return cc;

        // From GeoLocation middleware context
        if (context.Items.TryGetValue("GeoLocation", out var geoLocObj) && geoLocObj != null)
        {
            var countryProp = CountryCodePropertyCache.GetOrAdd(
                geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
            if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                return geoCC;
        }

        // From upstream headers
        return ResolveCountryFromHeaders(context);
    }

    /// <summary>
    ///     Build filtered non-PII signals dictionary from evidence signals.
    /// </summary>
    private static Dictionary<string, object> BuildImportantSignals(
        HttpContext context,
        IReadOnlyDictionary<string, object>? signals,
        ref string? countryCode)
    {
        var importantSignals = new Dictionary<string, object>();
        if (signals is { Count: > 0 })
        {
            importantSignals = signals
                .Where(s => AllowedSignalPrefixes.Any(p => s.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                            && !BlockedSignalKeys.Contains(s.Key))
                .Take(MaxSignalsPerDetection)
                .ToDictionary(s => s.Key, s => s.Value);
        }

        EnrichProtocol(context, importantSignals);

        var dashboardOptions = context.RequestServices.GetService<IOptions<StyloBotDashboardOptions>>()?.Value;
        if (dashboardOptions?.EnrichHumanSignals == true)
            EnrichFromRequest(context, importantSignals, ref countryCode);

        return importantSignals;
    }

    /// <summary>
    ///     Parse upstream gateway signals from X-Bot-Detection-Signals header.
    /// </summary>
    private Dictionary<string, object> ParseUpstreamSignals(HttpContext context)
    {
        var importantSignals = new Dictionary<string, object>();
        var upstreamSignalsHeader = context.Request.Headers["X-Bot-Detection-Signals"].FirstOrDefault();
        if (string.IsNullOrEmpty(upstreamSignalsHeader) || upstreamSignalsHeader.Length > MaxUpstreamSignalsHeaderLength)
            return importantSignals;

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

        return importantSignals;
    }

    // ─── Static utilities ────────────────────────────────────────────────

    /// <summary>
    ///     Check if an IP address is a local/private network address.
    ///     Supports both IPv4 and IPv6.
    /// </summary>
    internal static bool IsLocalIp(IPAddress? ip)
    {
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv6LinkLocal) return true;
        if (ip.IsIPv6SiteLocal) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

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

    private static string? ResolveCountryFromHeaders(HttpContext context)
    {
        var country = context.Request.Headers["X-Country"].FirstOrDefault()
                      ?? context.Request.Headers["CF-IPCountry"].FirstOrDefault();

        if (!string.IsNullOrEmpty(country) && country != "XX" && country != "LOCAL")
            return country;

        if (context.Items.TryGetValue("GeoDetection.CountryCode", out var geoCtx) &&
            geoCtx is string geoCountry && !string.IsNullOrEmpty(geoCountry) && geoCountry != "LOCAL")
            return geoCountry;

        return null;
    }

    private static string? SanitizeUserAgent(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        return EmailPattern().Replace(ua, "[email-redacted]");
    }

    private string GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var combined = $"{ip}:{ua}";

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static void EnrichProtocol(HttpContext context, Dictionary<string, object> signals)
    {
        if (signals.ContainsKey("h3.protocol") || signals.ContainsKey("h3.version") ||
            signals.ContainsKey("h2.fingerprint") || signals.ContainsKey("h2.settings_hash") ||
            signals.ContainsKey("h2.protocol"))
            return;

        var protocol = context.Request.Protocol;
        if (protocol.Contains("3", StringComparison.Ordinal))
            signals.TryAdd("h3.protocol", "h3");
        else if (protocol.Contains("2", StringComparison.Ordinal))
            signals.TryAdd("h2.protocol", "h2");
    }

    private static void EnrichFromRequest(HttpContext context, Dictionary<string, object> signals, ref string? countryCode)
    {
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

        if (countryCode == null &&
            context.Items.TryGetValue("GeoLocation", out var geoLocObj) && geoLocObj != null)
        {
            var countryProp = CountryCodePropertyCache.GetOrAdd(
                geoLocObj.GetType(), t => t.GetProperty("CountryCode"));
            if (countryProp?.GetValue(geoLocObj) is string geoCC && !string.IsNullOrEmpty(geoCC) && geoCC != "LOCAL")
                countryCode = geoCC;
        }

        if (countryCode == null)
            countryCode = ResolveCountryFromHeaders(context);
    }

    private static (string? Browser, string? Version) ParseBrowserFromUa(string ua)
    {
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
            return (null, null);

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
        var dot = full.IndexOf('.');
        return dot > 0 ? full[..dot] : full;
    }
}
