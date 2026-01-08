using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     View component for displaying bot detection results.
///     Works in two modes:
///     1. Inline with middleware: Reads from HttpContext.Items
///     2. Behind YARP proxy: Reads from X-Bot-Detection-* headers
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BotDetectionDetailsViewComponent(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public IViewComponentResult Invoke()
    {
        var context = _httpContextAccessor.HttpContext ?? HttpContext;
        if (context == null) return View(new DetectionDisplayModel());

        var model = ExtractDetectionData(context);
        return View(model);
    }

    /// <summary>
    ///     Extracts detection data from HttpContext.Items (inline) or headers (YARP proxy).
    /// </summary>
    private DetectionDisplayModel ExtractDetectionData(HttpContext context)
    {
        // Try inline mode first (HttpContext.Items)
        var model = TryExtractFromContextItems(context);
        if (model.HasData) return model;

        // Fallback to YARP headers
        return TryExtractFromYarpHeaders(context);
    }

    /// <summary>
    ///     Extracts detection data from HttpContext.Items (inline middleware mode).
    /// </summary>
    private DetectionDisplayModel TryExtractFromContextItems(HttpContext context)
    {
        // Check if we have AggregatedEvidence in context items
        // Note: The middleware stores this under "BotDetection.AggregatedEvidence"
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

        return new DetectionDisplayModel();
    }

    /// <summary>
    ///     Extracts detection data from YARP proxy headers.
    /// </summary>
    private DetectionDisplayModel TryExtractFromYarpHeaders(HttpContext context)
    {
        var headers = context.Request.Headers;

        // Check if YARP headers are present
        if (!headers.ContainsKey("X-Bot-Detection-Result")) return new DetectionDisplayModel();

        try
        {
            var botProbability = ParseDoubleHeader(headers, "X-Bot-Detection-Probability");
            var confidence = ParseDoubleHeader(headers, "X-Bot-Detection-Confidence");

            var model = new DetectionDisplayModel
            {
                IsBot = ParseBoolHeader(headers, "X-Bot-Detection-Result"),
                BotProbability = botProbability,
                Confidence = confidence,
                RiskBand = GetHeaderValue(headers, "X-Bot-Detection-RiskBand") ?? "Unknown",
                BotType = GetHeaderValue(headers, "X-Bot-Detection-BotType"),
                BotName = GetHeaderValue(headers, "X-Bot-Detection-BotName"),
                PolicyName = GetHeaderValue(headers, "X-Bot-Detection-Policy"),
                Action = GetHeaderValue(headers, "X-Bot-Detection-Action"),
                ProcessingTimeMs = ParseDoubleHeader(headers, "X-Bot-Detection-ProcessingMs"),
                RequestId = GetHeaderValue(headers, "X-Bot-Detection-RequestId"),
                Timestamp = DateTime.UtcNow,
                YarpCluster = GetHeaderValue(headers, "X-Bot-Detection-Cluster"),
                YarpDestination = GetHeaderValue(headers, "X-Bot-Detection-Destination"),
                Signatures = ExtractSignatures(context)
            };

            // Parse top reasons (JSON array)
            var reasonsJson = GetHeaderValue(headers, "X-Bot-Detection-Reasons");
            if (!string.IsNullOrEmpty(reasonsJson))
                try
                {
                    model.TopReasons = JsonSerializer.Deserialize<List<string>>(reasonsJson) ?? new List<string>();
                }
                catch
                {
                    // Ignore JSON parsing errors
                }

            // Parse detector contributions (JSON array)
            var contributionsJson = GetHeaderValue(headers, "X-Bot-Detection-Contributions");
            if (!string.IsNullOrEmpty(contributionsJson))
                try
                {
                    model.DetectorContributions =
                        JsonSerializer.Deserialize<List<DetectorContributionDisplay>>(contributionsJson)
                        ?? new List<DetectorContributionDisplay>();
                }
                catch
                {
                    // Ignore JSON parsing errors
                }

            return model;
        }
        catch
        {
            return new DetectionDisplayModel();
        }
    }

    private static string? GetHeaderValue(IHeaderDictionary headers, string key)
    {
        return headers.TryGetValue(key, out var value) ? value.ToString() : null;
    }

    private static bool ParseBoolHeader(IHeaderDictionary headers, string key)
    {
        var value = GetHeaderValue(headers, key);
        return bool.TryParse(value, out var result) && result;
    }

    private static double ParseDoubleHeader(IHeaderDictionary headers, string key)
    {
        var value = GetHeaderValue(headers, key);
        return double.TryParse(value, out var result) ? result : 0.0;
    }

    /// <summary>
    ///     Extracts multi-factor signatures from HttpContext and generates plain English explanation.
    /// </summary>
    private MultiFactorSignatureDisplay? ExtractSignatures(HttpContext context)
    {
        // Try to get signatures from context items (inline mode)
        if (context.Items.TryGetValue("BotDetection.Signatures", out var signaturesObj) &&
            signaturesObj is string signaturesJson)
            try
            {
                var signatures = JsonSerializer.Deserialize<Dictionary<string, string>>(signaturesJson);
                if (signatures != null) return BuildSignatureDisplay(signatures);
            }
            catch
            {
                // Ignore JSON parsing errors
            }

        // Try to get signatures from YARP headers
        var signatureHeader = GetHeaderValue(context.Request.Headers, "X-Bot-Detection-Signatures");
        if (!string.IsNullOrEmpty(signatureHeader))
            try
            {
                var signatures = JsonSerializer.Deserialize<Dictionary<string, string>>(signatureHeader);
                if (signatures != null) return BuildSignatureDisplay(signatures);
            }
            catch
            {
                // Ignore JSON parsing errors
            }

        return null;
    }

    /// <summary>
    ///     Builds a MultiFactorSignatureDisplay from signature dictionary with plain English explanation.
    /// </summary>
    private MultiFactorSignatureDisplay BuildSignatureDisplay(Dictionary<string, string> signatures)
    {
        var availableFactors = new List<string>();
        var explanations = new List<string>();

        // Extract and truncate signatures for display (first 12 chars)
        var primary = TruncateSignature(signatures.GetValueOrDefault("primary"));
        var ip = TruncateSignature(signatures.GetValueOrDefault("ip"));
        var ua = TruncateSignature(signatures.GetValueOrDefault("ua"));
        var clientSide = TruncateSignature(signatures.GetValueOrDefault("clientSide"));
        var plugin = TruncateSignature(signatures.GetValueOrDefault("plugin"));
        var ipSubnet = TruncateSignature(signatures.GetValueOrDefault("ipSubnet"));

        // Build list of available factors and explanations
        if (!string.IsNullOrEmpty(primary))
        {
            availableFactors.Add("Primary (IP+UA)");
            explanations.Add("Primary signature combines your IP address and browser to create a unique identifier.");
        }

        if (!string.IsNullOrEmpty(ip))
        {
            availableFactors.Add("IP");
            explanations.Add("IP signature tracks your network location - allows matching even when browser changes.");
        }

        if (!string.IsNullOrEmpty(ua))
        {
            availableFactors.Add("User-Agent");
            explanations.Add(
                "User-Agent signature tracks your browser - allows matching even when IP changes (mobile networks, VPNs).");
        }

        if (!string.IsNullOrEmpty(clientSide))
        {
            availableFactors.Add("Browser Fingerprint");
            explanations.Add(
                "Browser fingerprint is the most stable identifier - based on hardware rendering (Canvas, WebGL, AudioContext).");
        }

        if (!string.IsNullOrEmpty(plugin))
        {
            availableFactors.Add("Browser Configuration");
            explanations.Add("Browser configuration tracks installed plugins/extensions and language settings.");
        }

        if (!string.IsNullOrEmpty(ipSubnet))
        {
            availableFactors.Add("Network Subnet");
            explanations.Add("Network subnet groups traffic from the same datacenter or corporate network.");
        }

        // Generate overall explanation
        var overallExplanation = explanations.Count switch
        {
            0 => "No signature factors available.",
            1 => $"{explanations[0]} (Single factor - lower confidence)",
            2 =>
                $"Using {explanations.Count} factors for tracking: {string.Join(" ", explanations)} Multi-factor matching requires at least 2 factors to avoid false positives.",
            _ =>
                $"Using {explanations.Count} signature factors for robust tracking across network and browser changes. " +
                $"System requires at least 2 factors to match for positive identification. " +
                $"This handles real-world scenarios like mobile users (IP changes), browser updates (UA changes), " +
                $"while avoiding false positives from shared corporate networks."
        };

        return new MultiFactorSignatureDisplay
        {
            PrimarySignature = primary,
            IpSignature = ip,
            UaSignature = ua,
            ClientSideSignature = clientSide,
            PluginSignature = plugin,
            IpSubnetSignature = ipSubnet,
            FactorCount = availableFactors.Count,
            Explanation = overallExplanation,
            AvailableFactors = availableFactors
        };
    }

    /// <summary>
    ///     Truncates signature hash for display (first 12 characters).
    /// </summary>
    private static string? TruncateSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature))
            return null;

        return signature.Length > 12 ? signature.Substring(0, 12) + "..." : signature;
    }
}