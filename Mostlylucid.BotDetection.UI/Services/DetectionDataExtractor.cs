using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Extracts detection data from HttpContext into a display model.
///     Used by both BotDetectionDetailsViewComponent and BotDetectionHeaderViewComponent.
/// </summary>
public class DetectionDataExtractor
{
    /// <summary>
    ///     Extracts detection data from HttpContext.Items (inline) or YARP headers.
    /// </summary>
    public DetectionDisplayModel Extract(HttpContext context)
    {
        var model = TryExtractFromContextItems(context);
        if (model.HasData) return model;
        return TryExtractFromYarpHeaders(context);
    }

    private DetectionDisplayModel TryExtractFromContextItems(HttpContext context)
    {
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
        if (context.Items.TryGetValue("BotDetection.Signatures", out var signaturesObj) &&
            signaturesObj is string signaturesJson)
            try
            {
                var signatures = JsonSerializer.Deserialize<Dictionary<string, string>>(signaturesJson);
                if (signatures != null) return BuildSignatureDisplay(signatures, context);
            }
            catch { /* ignore */ }

        var signatureHeader = GetHeaderValue(context.Request.Headers, "X-Bot-Detection-Signatures");
        if (!string.IsNullOrEmpty(signatureHeader))
            try
            {
                var signatures = JsonSerializer.Deserialize<Dictionary<string, string>>(signatureHeader);
                if (signatures != null) return BuildSignatureDisplay(signatures, context);
            }
            catch { /* ignore */ }

        // Generate a fallback signature from IP+UA so the header bar and
        // SignalR tracking always have a value to work with.
        var fallback = GenerateFallbackSignature(context);
        if (fallback != null)
        {
            // Also write it back so DetectionBroadcastMiddleware uses the same value
            var fallbackDict = new Dictionary<string, string> { ["primary"] = fallback };
            context.Items["BotDetection.Signatures"] = JsonSerializer.Serialize(fallbackDict);
            return BuildSignatureDisplay(fallbackDict, context);
        }

        return null;
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
