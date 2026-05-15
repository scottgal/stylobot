using System.Net.Http.Json;
using Mostlylucid.BotDetection.Middleware;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Extracts detection data from HttpContext into a display model.
///     Supports three modes, tried in order:
///     <list type="number">
///         <item>HttpContext.Items - populated by inline BotDetectionMiddleware</item>
///         <item>X-Bot-Detection-* request headers - set by YARP gateway upstream</item>
///         <item>API call to the configured <see cref="DetectionApiOptions.Endpoint"/> - populated by
///             <see cref="SbDetectionMiddleware"/> running before tag helpers process</item>
///     </list>
///     <para>
///     Tag helpers call the synchronous <see cref="Extract"/> method. API-mode results are cached
///     into <c>HttpContext.Items</c> by <see cref="SbDetectionMiddleware"/> before the request
///     reaches Razor, so <see cref="Extract"/> always reads from Items (never makes async HTTP calls).
///     </para>
/// </summary>
public class DetectionDataExtractor
{
    // Cache key used by SbDetectionMiddleware to store pre-fetched API results in Items.
    internal const string ApiCacheKey = "BotDetection.ApiResult";

    private static readonly object _apiSentinel = new();

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly string? _apiEndpoint;

    /// <summary>
    ///     Creates the extractor. When <paramref name="httpClientFactory"/> and
    ///     <paramref name="options"/> are provided and an endpoint is configured, API mode is enabled.
    ///     All parameters are optional so the extractor works without API mode registered.
    /// </summary>
    public DetectionDataExtractor(
        IHttpClientFactory? httpClientFactory = null,
        IOptions<DetectionApiOptions>? options = null)
    {
        _httpClientFactory = httpClientFactory;
        _apiEndpoint = options?.Value.Endpoint;
    }

    /// <summary>
    ///     Extracts detection data from HttpContext.Items (inline), YARP headers, or
    ///     a pre-fetched API result written by <see cref="SbDetectionMiddleware"/>.
    ///     This method is synchronous and safe to call from tag helper <c>Process()</c>.
    /// </summary>
    public DetectionDisplayModel Extract(HttpContext context)
    {
        var model = TryExtractFromContextItems(context);
        if (model.HasData) return model;
        return TryExtractFromYarpHeaders(context);
    }

    /// <summary>
    ///     Ensures detection data is populated in <c>HttpContext.Items</c>.
    ///     If Items already has data (inline mode) or headers are present (YARP mode), returns immediately.
    ///     Otherwise, if an API endpoint is configured, calls it and caches the result in Items
    ///     so that subsequent synchronous <see cref="Extract"/> calls find the data.
    ///     Called by <see cref="SbDetectionMiddleware"/> early in the pipeline.
    /// </summary>
    public async Task EnsurePopulatedAsync(HttpContext context)
    {
        // Already have data from inline middleware or YARP headers - nothing to do.
        var model = TryExtractFromContextItems(context);
        if (model.HasData) return;

        var headerModel = TryExtractFromYarpHeaders(context);
        if (headerModel.HasData) return;

        // API mode: call the configured endpoint and cache in Items.
        if (string.IsNullOrEmpty(_apiEndpoint) || _httpClientFactory == null)
            return;

        // Guard against concurrent calls on the same HttpContext.
        // If another concurrent invocation already claimed the slot, return immediately.
        if (context.Items.ContainsKey(ApiCacheKey))
            return;

        context.Items[ApiCacheKey] = _apiSentinel; // claim the slot

        var apiModel = await TryFetchFromApiAsync(context);
        if (apiModel is { HasData: true })
        {
            // Store under the Items key that TryExtractFromContextItems checks via ApiCacheKey,
            // but since the API returns a ready model we store it directly and short-circuit.
            context.Items[ApiCacheKey] = apiModel;
        }
        // If fetch failed, leave the sentinel - prevents retry storms on the same request
    }

    private DetectionDisplayModel TryExtractFromContextItems(HttpContext context)
    {
        // Check for a pre-fetched API result stored by SbDetectionMiddleware.
        if (context.Items.TryGetValue(ApiCacheKey, out var cachedApiResult) &&
            cachedApiResult is DetectionDisplayModel cachedModel)
            return cachedModel;

        if (context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
            return new DetectionDisplayModel
            {
                IsBot = evidence.BotProbability > 0.5,
                BotProbability = evidence.BotProbability,
                Confidence = evidence.Confidence,
                RiskBand = evidence.RiskBand.ToString(),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                PolicyName = evidence.PolicyName,
                Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName,
                ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                TopReasons = evidence.Contributions
                    .Where(c => !string.IsNullOrEmpty(c.Reason))
                    .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                    .Take(5)
                    .Select(c => c.Reason!)
                    .ToList(),
                DetectorContributions = evidence.Contributions
                    .GroupBy(c => c.DetectorName)
                    .Select(g => new DetectorContributionDisplay
                    {
                        Name = g.Key,
                        Category = g.First().Category,
                        ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                        Weight = g.Sum(c => c.Weight),
                        Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                        Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                        ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                        Priority = g.First().Priority
                    })
                    .OrderByDescending(d => Math.Abs(d.Contribution))
                    .ToList(),
                RawSignals = evidence.Signals?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, object>(),
                FingerprintHash = context.Items.TryGetValue("BotDetection.FingerprintHash", out var fpHashObj) && fpHashObj != null
                    ? fpHashObj.ToString()
                    : null,
                RequestId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow,
                YarpCluster = context.Items.TryGetValue("Yarp.Cluster", out var cluster) && cluster != null
                    ? cluster.ToString()
                    : null,
                YarpDestination = context.Items.TryGetValue("Yarp.Destination", out var dest) && dest != null
                    ? dest.ToString()
                    : null,
                Signatures = ExtractSignatures(context)
            };

        // Fallback: upstream trust sets lightweight BotDetectionResult (no AggregatedEvidence)
        if (context.Items.TryGetValue("BotDetectionResult", out var resultObj) &&
            resultObj is Mostlylucid.BotDetection.Models.BotDetectionResult result)
        {
            var confidence = result.ConfidenceScore;
            var riskBand = confidence switch
            {
                >= 0.85 => "VeryHigh",
                >= 0.7 => "High",
                >= 0.4 => "Medium",
                >= 0.2 => "Low",
                _ => "VeryLow"
            };

            return new DetectionDisplayModel
            {
                IsBot = result.IsBot,
                BotProbability = confidence,
                Confidence = confidence,
                RiskBand = riskBand,
                BotType = result.BotType?.ToString(),
                BotName = result.BotName,
                PolicyName = context.Items.TryGetValue("BotDetection.PolicyName", out var pn) ? pn?.ToString() : "upstream",
                ProcessingTimeMs = 0,
                TopReasons = [],
                DetectorContributions = [],
                RequestId = context.TraceIdentifier,
                Timestamp = DateTime.UtcNow,
                Signatures = ExtractSignatures(context)
            };
        }

        return new DetectionDisplayModel();
    }

    private async Task<DetectionDisplayModel?> TryFetchFromApiAsync(HttpContext context)
    {
        try
        {
            var client = _httpClientFactory!.CreateClient("stylobot");

            // Forward the visitor's IP and User-Agent so the API can fingerprint correctly.
            var request = new HttpRequestMessage(HttpMethod.Get, _apiEndpoint);

            var remoteIp = context.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(remoteIp))
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteIp);

            var userAgent = context.Request.Headers.UserAgent.ToString();
            if (!string.IsNullOrEmpty(userAgent))
                request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

            using var response = await client.SendAsync(request, context.RequestAborted);
            if (!response.IsSuccessStatusCode) return null;

            var model = await response.Content.ReadFromJsonAsync<DetectionDisplayModel>(
                cancellationToken: context.RequestAborted);
            return model;
        }
        catch
        {
            // API unavailable - fall through, tag helpers will see no data (fail-open).
            return null;
        }
    }

    private DetectionDisplayModel TryExtractFromYarpHeaders(HttpContext context)
    {
        var headers = context.Request.Headers;
        if (!headers.ContainsKey("X-Bot-Detection-Result")) return new DetectionDisplayModel();

        try
        {
            var model = new DetectionDisplayModel
            {
                IsBot = ParseBoolHeader(headers, "X-Bot-Detection-Result"),
                BotProbability = ParseDoubleHeader(headers, "X-Bot-Detection-Probability"),
                Confidence = ParseDoubleHeader(headers, "X-Bot-Detection-Confidence"),
                RiskBand = GetHeaderValue(headers, "X-Bot-Detection-RiskBand") ?? "Unknown",
                BotType = GetHeaderValue(headers, "X-Bot-Detection-BotType"),
                BotName = GetHeaderValue(headers, "X-Bot-Detection-BotName"),
                PolicyName = GetHeaderValue(headers, "X-Bot-Detection-Policy"),
                Action = GetHeaderValue(headers, "X-Bot-Detection-Action"),
                ProcessingTimeMs = ParseDoubleHeader(headers, "X-Bot-Detection-ProcessingMs"),
                FingerprintHash = GetHeaderValue(headers, "X-Bot-Detection-FingerprintHash"),
                RequestId = GetHeaderValue(headers, "X-Bot-Detection-RequestId"),
                Timestamp = DateTime.UtcNow,
                YarpCluster = GetHeaderValue(headers, "X-Bot-Detection-Cluster"),
                YarpDestination = GetHeaderValue(headers, "X-Bot-Detection-Destination"),
                Signatures = ExtractSignatures(context)
            };

            var reasonsJson = GetHeaderValue(headers, "X-Bot-Detection-Reasons");
            if (!string.IsNullOrEmpty(reasonsJson))
                try { model.TopReasons = JsonSerializer.Deserialize<List<string>>(reasonsJson) ?? []; }
                catch { /* ignore */ }

            var contributionsJson = GetHeaderValue(headers, "X-Bot-Detection-Contributions");
            if (!string.IsNullOrEmpty(contributionsJson))
                try { model.DetectorContributions = JsonSerializer.Deserialize<List<DetectorContributionDisplay>>(contributionsJson) ?? []; }
                catch { /* ignore */ }

            var signalsJson = GetHeaderValue(headers, "X-Bot-Detection-Signals");
            if (!string.IsNullOrEmpty(signalsJson))
                try { model.RawSignals = JsonSerializer.Deserialize<Dictionary<string, object>>(signalsJson) ?? []; }
                catch { /* ignore */ }

            return model;
        }
        catch { return new DetectionDisplayModel(); }
    }

    private MultiFactorSignatureDisplay? ExtractSignatures(HttpContext context)
    {
        // Canonical: read the signature the orchestrator's foundation wave wrote.
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            && evObj is AggregatedEvidence ev)
        {
            if (ev.Signals.TryGetValue(Mostlylucid.BotDetection.Models.SignalKeys.SignatureMultifactor, out var multiObj)
                && multiObj is MultiFactorSignatures multi
                && !string.IsNullOrEmpty(multi.PrimarySignature))
            {
                return BuildSignatureDisplay(MultiFactorToDict(multi), context);
            }

            if (ev.Signals.TryGetValue(Mostlylucid.BotDetection.Models.SignalKeys.PrimarySignature, out var primaryObj)
                && primaryObj is string primary && !string.IsNullOrEmpty(primary))
            {
                return BuildSignatureDisplay(new Dictionary<string, string> { ["primary"] = primary }, context);
            }
        }

        // Gateway-trust path: signatures forwarded as a JSON header by an upstream gateway.
        var signatureHeader = GetHeaderValue(context.Request.Headers, "X-Bot-Detection-Signatures");
        if (!string.IsNullOrEmpty(signatureHeader))
            try
            {
                var signatures = JsonSerializer.Deserialize<Dictionary<string, string>>(signatureHeader);
                if (signatures != null) return BuildSignatureDisplay(signatures, context);
            }
            catch { /* ignore */ }

        // No detection ran and no upstream sigs: synthesize a transient IP+UA fallback so the
        // header bar and SignalR tracking always have a value. Not stored back; downstream
        // consumers compute their own fallbacks via the same helper.
        var fallback = GenerateFallbackSignature(context);
        if (fallback != null)
            return BuildSignatureDisplay(new Dictionary<string, string> { ["primary"] = fallback }, context);

        return null;
    }

    private static Dictionary<string, string> MultiFactorToDict(MultiFactorSignatures m)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(m.PrimarySignature)) dict["primary"] = m.PrimarySignature;
        if (!string.IsNullOrEmpty(m.IpSignature)) dict["ip"] = m.IpSignature;
        if (!string.IsNullOrEmpty(m.UaSignature)) dict["ua"] = m.UaSignature;
        if (!string.IsNullOrEmpty(m.ClientSideSignature)) dict["client"] = m.ClientSideSignature;
        return dict;
    }

    private static string? GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString();
        var ua = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(ua)) return null;
        var combined = $"{ip}:{ua}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static MultiFactorSignatureDisplay BuildSignatureDisplay(Dictionary<string, string> signatures, HttpContext? context = null)
    {
        var availableFactors = new List<string>();

        var primary = Truncate(signatures.GetValueOrDefault("primary"));
        var ip = Truncate(signatures.GetValueOrDefault("ip"));
        var ua = Truncate(signatures.GetValueOrDefault("ua"));
        var clientSide = Truncate(signatures.GetValueOrDefault("clientSide"));
        var plugin = Truncate(signatures.GetValueOrDefault("plugin"));
        var ipSubnet = Truncate(signatures.GetValueOrDefault("ipSubnet"));

        if (!string.IsNullOrEmpty(primary)) availableFactors.Add("Primary (IP+UA)");
        if (!string.IsNullOrEmpty(ip)) availableFactors.Add("IP");
        if (!string.IsNullOrEmpty(ua)) availableFactors.Add("User-Agent");
        if (!string.IsNullOrEmpty(clientSide)) availableFactors.Add("Browser Fingerprint");
        if (!string.IsNullOrEmpty(plugin)) availableFactors.Add("Browser Configuration");
        if (!string.IsNullOrEmpty(ipSubnet)) availableFactors.Add("Network Subnet");

        var explanation = availableFactors.Count switch
        {
            0 => "No signature factors available.",
            1 => $"{availableFactors.Count} factor tracking (lower confidence).",
            _ => $"{availableFactors.Count}-factor tracking for robust identification."
        };

        DateTimeOffset? firstSeen = null;
        DateTimeOffset? lastSeen = null;
        int? totalHits = null;

        if (context != null)
        {
            if (context.Items.TryGetValue("BotDetection.Signature.FirstSeen", out var fs) && fs is DateTimeOffset fso) firstSeen = fso;
            if (context.Items.TryGetValue("BotDetection.Signature.LastSeen", out var ls) && ls is DateTimeOffset lso) lastSeen = lso;
            if (context.Items.TryGetValue("BotDetection.Signature.TotalHits", out var h) && h is int hits) totalHits = hits;
        }

        return new MultiFactorSignatureDisplay
        {
            PrimarySignature = primary,
            IpSignature = ip,
            UaSignature = ua,
            ClientSideSignature = clientSide,
            PluginSignature = plugin,
            IpSubnetSignature = ipSubnet,
            FactorCount = availableFactors.Count,
            Explanation = explanation,
            AvailableFactors = availableFactors,
            FirstSeen = firstSeen,
            LastSeen = lastSeen,
            TotalHits = totalHits
        };
    }

    private static string? Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? null : s.Length > 12 ? s[..12] + "..." : s;

    private static string? GetHeaderValue(IHeaderDictionary headers, string key) =>
        headers.TryGetValue(key, out var value) ? value.ToString() : null;

    private static bool ParseBoolHeader(IHeaderDictionary headers, string key) =>
        bool.TryParse(GetHeaderValue(headers, key), out var result) && result;

    private static double ParseDoubleHeader(IHeaderDictionary headers, string key) =>
        double.TryParse(GetHeaderValue(headers, key), out var result) ? result : 0.0;
}
