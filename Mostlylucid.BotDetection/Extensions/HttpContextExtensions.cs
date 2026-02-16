using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Extension methods for easy access to bot detection results from HttpContext
/// </summary>
public static partial class HttpContextExtensions
{
    /// <summary>
    ///     Gets the bot detection result from the current request.
    ///     Returns null if bot detection middleware hasn't run.
    /// </summary>
    public static BotDetectionResult? GetBotDetectionResult(this HttpContext context)
    {
        return context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var result)
            ? result as BotDetectionResult
            : null;
    }

    /// <summary>
    ///     Returns true if the current request was detected as a bot.
    ///     Returns false if not a bot OR if detection hasn't run.
    /// </summary>
    public static bool IsBot(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.IsBot ?? false;
    }

    /// <summary>
    ///     Returns true if the current request is from a verified good bot (e.g., Googlebot).
    /// </summary>
    public static bool IsVerifiedBot(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        return result?.BotType == BotType.VerifiedBot;
    }

    /// <summary>
    ///     Returns true if the current request is from a search engine bot.
    /// </summary>
    public static bool IsSearchEngineBot(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        return result?.BotType == BotType.SearchEngine || result?.BotType == BotType.VerifiedBot;
    }

    /// <summary>
    ///     Returns true if the current request is from a potentially malicious bot.
    /// </summary>
    public static bool IsMaliciousBot(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        return result?.BotType == BotType.MaliciousBot;
    }

    /// <summary>
    ///     Returns true if the current request appears to be from a human visitor.
    /// </summary>
    public static bool IsHuman(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        return result != null && !result.IsBot;
    }

    /// <summary>
    ///     Gets the bot probability score (0.0 to 1.0) — how likely this request is from a bot.
    ///     Returns 0.0 if detection hasn't run.
    /// </summary>
    public static double GetBotProbability(this HttpContext context)
    {
        if (context.Items.TryGetValue(Middleware.BotDetectionMiddleware.AggregatedEvidenceKey, out var obj) &&
            obj is Orchestration.AggregatedEvidence evidence)
            return evidence.BotProbability;
        return context.GetBotDetectionResult()?.ConfidenceScore ?? 0.0;
    }

    /// <summary>
    ///     Gets the detection confidence (0.0 to 1.0) — how certain the system is in its verdict,
    ///     independent of the bot probability. High confidence with low probability means
    ///     "we're very sure this is human". Based on detector coverage, agreement, and evidence weight.
    ///     Returns 0.0 if detection hasn't run.
    /// </summary>
    public static double GetDetectionConfidence(this HttpContext context)
    {
        if (context.Items.TryGetValue(Middleware.BotDetectionMiddleware.AggregatedEvidenceKey, out var obj) &&
            obj is Orchestration.AggregatedEvidence evidence)
            return evidence.Confidence;
        return 0.0;
    }

    /// <summary>
    ///     Gets the bot confidence score (0.0 to 1.0).
    ///     Returns 0.0 if detection hasn't run.
    /// </summary>
    /// <remarks>
    ///     This returns the bot probability (likelihood of being a bot), not the detection confidence.
    ///     For decision certainty, use <see cref="GetDetectionConfidence" />.
    ///     For bot likelihood, prefer <see cref="GetBotProbability" />.
    /// </remarks>
    public static double GetBotConfidence(this HttpContext context)
    {
        // Check AggregatedEvidence first (has correct separated values)
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var obj) &&
            obj is AggregatedEvidence evidence)
            return evidence.BotProbability;
        return context.GetBotDetectionResult()?.ConfidenceScore ?? 0.0;
    }

    /// <summary>
    ///     Gets the detected bot type, or null if not a bot or detection hasn't run.
    /// </summary>
    public static BotType? GetBotType(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.BotType;
    }

    /// <summary>
    ///     Gets the detected bot name (e.g., "Googlebot", "AhrefsBot"), or null if unknown.
    /// </summary>
    public static string? GetBotName(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.BotName;
    }

    /// <summary>
    ///     Returns true if the request should be allowed (human or verified good bot).
    ///     Useful for protecting sensitive endpoints while allowing search engines.
    /// </summary>
    public static bool ShouldAllowRequest(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        if (result == null) return true; // Allow if detection hasn't run

        // Allow humans and verified bots
        return !result.IsBot || result.BotType == BotType.VerifiedBot;
    }

    /// <summary>
    ///     Returns true if the request should be blocked (detected as bot, not verified).
    /// </summary>
    public static bool ShouldBlockRequest(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        if (result == null) return false;

        return result.IsBot && result.BotType != BotType.VerifiedBot;
    }

    /// <summary>
    ///     Gets the primary detection category (e.g., "UserAgent", "IP", "Header").
    /// </summary>
    /// <returns>The category string from the highest-confidence reason, or null if not available.</returns>
    public static string? GetBotCategory(this HttpContext context)
    {
        if (context.Items.TryGetValue(BotDetectionMiddleware.BotCategoryKey, out var category))
            return category as string;

        // Fallback to computing from result if not set
        var result = context.GetBotDetectionResult();
        if (result?.Reasons.Count > 0)
            return result.Reasons.OrderByDescending(r => r.ConfidenceImpact).First().Category;

        return null;
    }

    /// <summary>
    ///     Gets all detection reasons.
    /// </summary>
    /// <returns>List of DetectionReason objects, or an empty list if not available.</returns>
    public static IReadOnlyList<DetectionReason> GetDetectionReasons(this HttpContext context)
    {
        if (context.Items.TryGetValue(BotDetectionMiddleware.DetectionReasonsKey, out var reasons)
            && reasons is IReadOnlyList<DetectionReason> list)
            return list;

        // Fallback to computing from result
        var result = context.GetBotDetectionResult();
        return result?.Reasons ?? (IReadOnlyList<DetectionReason>)Array.Empty<DetectionReason>();
    }

    /// <summary>
    ///     Checks if the bot is a social media crawler (e.g., Facebook, Twitter).
    /// </summary>
    public static bool IsSocialMediaBot(this HttpContext context)
    {
        return context.GetBotType() == BotType.SocialMediaBot;
    }

    /// <summary>
    ///     Checks if the bot probability meets a minimum threshold.
    /// </summary>
    /// <param name="context">The HttpContext</param>
    /// <param name="threshold">Minimum bot probability threshold (0.0-1.0)</param>
    /// <returns>True if detected as a bot with probability at or above the threshold.</returns>
    public static bool IsBotWithConfidence(this HttpContext context, double threshold)
    {
        return context.IsBot() && context.GetBotProbability() >= threshold;
    }

    // ==========================================
    // Risk Assessment Methods
    // ==========================================

    /// <summary>
    ///     Gets the risk band for the current request based on detection results.
    ///     Use this to determine how to handle the request (allow, challenge, throttle, block).
    /// </summary>
    /// <returns>A RiskBand indicating the risk level of the request.</returns>
    public static RiskBand GetRiskBand(this HttpContext context)
    {
        // Prefer the orchestrator's already-computed RiskBand from AggregatedEvidence
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var obj) &&
            obj is AggregatedEvidence evidence)
            return evidence.RiskBand;

        var result = context.GetBotDetectionResult();
        if (result == null)
            return RiskBand.Unknown;

        // Verified bots are low risk
        if (result.BotType == BotType.VerifiedBot || result.BotType == BotType.SearchEngine)
            return RiskBand.Low;

        // Social media bots are generally low risk
        if (result.BotType == BotType.SocialMediaBot)
            return RiskBand.Low;

        // Not a bot = low risk
        if (!result.IsBot)
            return RiskBand.Low;

        // Malicious bots are high risk
        if (result.BotType == BotType.MaliciousBot)
            return RiskBand.High;

        // Score-based risk assessment
        return result.ConfidenceScore switch
        {
            >= 0.9 => RiskBand.High, // Very confident bot
            >= 0.7 => RiskBand.Medium, // Likely bot
            >= 0.5 => RiskBand.Elevated, // Possibly bot, challenge recommended
            _ => RiskBand.Low // Probably human
        };
    }

    /// <summary>
    ///     Returns true if the request should be challenged (e.g., with CAPTCHA, proof-of-work).
    ///     Returns true for Medium and Elevated risk bands.
    ///     For High risk, you may want to block instead of challenge.
    /// </summary>
    /// <returns>True if the request should be challenged rather than immediately allowed/blocked.</returns>
    public static bool ShouldChallengeRequest(this HttpContext context)
    {
        var riskBand = context.GetRiskBand();
        return riskBand == RiskBand.Elevated || riskBand == RiskBand.Medium;
    }

    /// <summary>
    ///     Returns true if the request should be rate-limited or throttled.
    ///     Returns true for Elevated risk and above.
    /// </summary>
    public static bool ShouldThrottleRequest(this HttpContext context)
    {
        var riskBand = context.GetRiskBand();
        return riskBand >= RiskBand.Elevated;
    }

    /// <summary>
    ///     Gets the recommended action for handling the current request.
    /// </summary>
    public static RecommendedAction GetRecommendedAction(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        if (result == null)
            return RecommendedAction.Allow;

        var riskBand = context.GetRiskBand();

        return riskBand switch
        {
            RiskBand.High => RecommendedAction.Block,
            RiskBand.Medium => RecommendedAction.Challenge,
            RiskBand.Elevated => RecommendedAction.Throttle,
            RiskBand.Low => RecommendedAction.Allow,
            _ => RecommendedAction.Allow
        };
    }

    /// <summary>
    ///     Gets an inconsistency score based on detection reasons.
    ///     Higher scores indicate more signal inconsistencies (UA vs headers, etc.).
    /// </summary>
    /// <returns>Inconsistency score from 0 (consistent) to 100 (highly inconsistent).</returns>
    public static int GetInconsistencyScore(this HttpContext context)
    {
        var reasons = context.GetDetectionReasons();
        var inconsistencyReasons = reasons.Where(r => r.Category == "Inconsistency").ToList();

        if (inconsistencyReasons.Count == 0)
            return 0;

        // Sum up confidence impacts from inconsistency reasons, scale to 0-100
        var totalImpact = inconsistencyReasons.Sum(r => r.ConfidenceImpact);
        return Math.Min(100, (int)(totalImpact * 100));
    }

    /// <summary>
    ///     Gets the browser integrity score from client-side fingerprinting.
    ///     Returns null if client-side detection hasn't been performed.
    /// </summary>
    public static int? GetBrowserIntegrityScore(this HttpContext context)
    {
        var reasons = context.GetDetectionReasons();
        var clientSideReason = reasons.FirstOrDefault(r =>
            r.Category == "ClientSide" && r.Detail.Contains("integrity score"));

        if (clientSideReason == null)
            return null;

        // Parse score from detail string like "Low browser integrity score: 45/100"
        var match = IntegrityScoreRegex().Match(clientSideReason.Detail);

        if (match.Success && int.TryParse(match.Groups[1].Value, out var score))
            return score;

        return null;
    }

    /// <summary>
    ///     Gets the headless browser likelihood from client-side fingerprinting.
    ///     Returns null if client-side detection hasn't been performed.
    /// </summary>
    public static double? GetHeadlessLikelihood(this HttpContext context)
    {
        var reasons = context.GetDetectionReasons();
        var headlessReason = reasons.FirstOrDefault(r =>
            r.Category == "ClientSide" && r.Detail.Contains("Headless"));

        if (headlessReason == null)
            return null;

        // Parse likelihood from detail string like "Headless browser detected (likelihood: 0.85)"
        var match = HeadlessLikelihoodRegex().Match(headlessReason.Detail);

        if (match.Success && double.TryParse(match.Groups[1].Value, out var likelihood))
            return likelihood;

        return headlessReason.ConfidenceImpact;
    }

    [GeneratedRegex(@"(\d+)/100")]
    private static partial Regex IntegrityScoreRegex();

    [GeneratedRegex(@"likelihood:\s*([\d.]+)")]
    private static partial Regex HeadlessLikelihoodRegex();

    // ==========================================
    // Signal Access Methods
    // ==========================================

    /// <summary>
    ///     Gets the full AggregatedEvidence from the detection pipeline.
    ///     Returns null if detection hasn't run.
    /// </summary>
    public static AggregatedEvidence? GetAggregatedEvidence(this HttpContext context)
    {
        return context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var obj)
            && obj is AggregatedEvidence evidence
                ? evidence
                : null;
    }

    /// <summary>
    ///     Gets all detection signals from the pipeline as a read-only dictionary.
    ///     Signal keys are defined in <see cref="SignalKeys" />.
    ///     Returns an empty dictionary if detection hasn't run.
    /// </summary>
    /// <example>
    ///     var signals = context.GetSignals();
    ///     if (signals.TryGetValue(SignalKeys.GeoCountryCode, out var country))
    ///         logger.LogInformation("Request from {Country}", country);
    /// </example>
    public static IReadOnlyDictionary<string, object> GetSignals(this HttpContext context)
    {
        return context.GetAggregatedEvidence()?.Signals
               ?? (IReadOnlyDictionary<string, object>)new Dictionary<string, object>();
    }

    /// <summary>
    ///     Gets a typed signal value by key.
    ///     Returns default(T) if the signal doesn't exist or is not of type T.
    /// </summary>
    /// <example>
    ///     var isVpn = context.GetSignal&lt;bool&gt;(SignalKeys.GeoIsVpn);
    ///     var country = context.GetSignal&lt;string&gt;(SignalKeys.GeoCountryCode);
    ///     var botRate = context.GetSignal&lt;double&gt;(SignalKeys.GeoCountryBotRate);
    /// </example>
    public static T? GetSignal<T>(this HttpContext context, string signalKey)
    {
        var signals = context.GetSignals();
        if (signals.TryGetValue(signalKey, out var value))
        {
            if (value is T typed)
                return typed;

            // Handle common type conversions (signals may be stored as different numeric types)
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                return default;
            }
        }

        return default;
    }

    /// <summary>
    ///     Checks whether a specific signal exists in the detection results.
    /// </summary>
    public static bool HasSignal(this HttpContext context, string signalKey)
    {
        return context.GetSignals().ContainsKey(signalKey);
    }

    // ==========================================
    // Geographic / Network Access Methods
    // ==========================================

    /// <summary>
    ///     Gets the detected country code (ISO 3166-1 alpha-2) for the request.
    ///     Requires GeoDetection contributor to be registered.
    /// </summary>
    public static string? GetCountryCode(this HttpContext context)
    {
        return context.GetSignal<string>(SignalKeys.GeoCountryCode);
    }

    /// <summary>
    ///     Returns true if the request originates from a VPN connection.
    ///     Requires GeoDetection contributor to be registered.
    /// </summary>
    public static bool IsVpn(this HttpContext context)
    {
        return context.GetSignal<bool>(SignalKeys.GeoIsVpn);
    }

    /// <summary>
    ///     Returns true if the request originates from a proxy server.
    ///     Requires GeoDetection contributor to be registered.
    /// </summary>
    public static bool IsProxy(this HttpContext context)
    {
        return context.GetSignal<bool>(SignalKeys.GeoIsProxy);
    }

    /// <summary>
    ///     Returns true if the request originates from a Tor exit node.
    ///     Requires GeoDetection contributor to be registered.
    /// </summary>
    public static bool IsTor(this HttpContext context)
    {
        return context.GetSignal<bool>(SignalKeys.GeoIsTor);
    }

    /// <summary>
    ///     Returns true if the request originates from a datacenter/hosting provider (AWS, Azure, GCP, etc.).
    /// </summary>
    public static bool IsDatacenter(this HttpContext context)
    {
        return context.GetSignal<bool>(SignalKeys.GeoIsHosting)
               || context.GetSignal<bool>(SignalKeys.IpIsDatacenter);
    }

    /// <summary>
    ///     Gets the bot rate for the request's country of origin (0.0-1.0).
    ///     Higher values indicate countries that produce more bot traffic.
    ///     Requires GeoDetection contributor to be registered.
    /// </summary>
    public static double GetCountryBotRate(this HttpContext context)
    {
        return context.GetSignal<double>(SignalKeys.GeoCountryBotRate);
    }
}

// NOTE: RiskBand and RecommendedAction enums are now in Mostlylucid.BotDetection.Orchestration.DetectionContribution
// Use: using Mostlylucid.BotDetection.Orchestration;