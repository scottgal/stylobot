using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Handles learning events and updates the weight store.
///     This is the key component that feeds learned patterns back to static analyzers.
/// </summary>
/// <remarks>
///     <para>
///         The learning feedback loop:
///         <list type="number">
///             <item>Detectors run in the hot path, produce AggregatedEvidence</item>
///             <item>High-confidence detections publish learning events</item>
///             <item>This handler processes events in the slow path (background)</item>
///             <item>Handler extracts signatures and updates weights</item>
///             <item>Static analyzers read weights to adjust their confidence</item>
///         </list>
///     </para>
///     <para>
///         Signatures are computed from:
///         <list type="bullet">
///             <item>User-Agent patterns (normalized/hashed)</item>
///             <item>IP address ranges (CIDR blocks)</item>
///             <item>Request path patterns</item>
///             <item>Behavioral hashes (from timing, navigation patterns)</item>
///             <item>Combined multi-factor signatures</item>
///         </list>
///     </para>
/// </remarks>
public class SignatureFeedbackHandler : ILearningEventHandler
{
    private readonly ILogger<SignatureFeedbackHandler> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IWeightStore _weightStore;

    public SignatureFeedbackHandler(
        ILogger<SignatureFeedbackHandler> logger,
        IWeightStore weightStore,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _weightStore = weightStore;
        _options = options.Value;
    }

    /// <summary>
    ///     Event types this handler processes.
    /// </summary>
    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection,
        LearningEventType.FullDetection,
        LearningEventType.SignatureFeedback,
        LearningEventType.UserFeedback
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        try
        {
            switch (evt.Type)
            {
                case LearningEventType.HighConfidenceDetection:
                    await HandleHighConfidenceDetection(evt, ct);
                    break;

                case LearningEventType.FullDetection:
                    await HandleFullDetection(evt, ct);
                    break;

                case LearningEventType.SignatureFeedback:
                    await HandleSignatureFeedback(evt, ct);
                    break;

                case LearningEventType.UserFeedback:
                    await HandleUserFeedback(evt, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling learning event: {Type}", evt.Type);
        }
    }

    private async Task HandleHighConfidenceDetection(LearningEvent evt, CancellationToken ct)
    {
        // evt.Confidence is detection certainty, evt.Label indicates bot (true) or human (false)
        // We want high-confidence learning for BOTH bot and human detections
        if (!evt.Confidence.HasValue || evt.Confidence.Value < 0.5)
            return;

        // Use label if available; for high-confidence events, botProbability in metadata is more accurate
        var wasBot = evt.Label ?? false;
        if (evt.Metadata?.TryGetValue("botProbability", out var probObj) == true && probObj is double botProb)
            wasBot = botProb >= 0.5;

        // Extract signatures from metadata
        var signatures = ExtractSignatures(evt);

        foreach (var (sigType, signature) in signatures)
            await _weightStore.RecordObservationAsync(
                sigType,
                signature,
                wasBot,
                evt.Confidence.Value,
                ct);

        _logger.LogDebug(
            "High-confidence detection: {Count} signatures updated, wasBot={WasBot}, confidence={Confidence:F2}",
            signatures.Count, wasBot, evt.Confidence);
    }

    private async Task HandleFullDetection(LearningEvent evt, CancellationToken ct)
    {
        // For full detections, we only record if confidence is reasonable
        if (!evt.Confidence.HasValue || evt.Confidence.Value < 0.3)
            return;

        // Use botProbability from metadata for accurate bot/human determination
        var wasBot = evt.Label ?? false;
        if (evt.Metadata?.TryGetValue("botProbability", out var probObj) == true && probObj is double botProb)
            wasBot = botProb >= 0.5;

        var signatures = ExtractSignatures(evt);

        // Use lower weight for full detections (less confident)
        foreach (var (sigType, signature) in signatures)
            await _weightStore.RecordObservationAsync(
                sigType,
                signature,
                wasBot,
                evt.Confidence.Value * 0.5, // Reduce weight
                ct);
    }

    private async Task HandleSignatureFeedback(LearningEvent evt, CancellationToken ct)
    {
        // Direct signature feedback from other handlers
        if (string.IsNullOrEmpty(evt.Pattern))
            return;

        var sigType = evt.Metadata?.TryGetValue("signatureType", out var st) == true
            ? st?.ToString() ?? SignatureTypes.CombinedSignature
            : SignatureTypes.CombinedSignature;

        // Get wasBot from Label, or from metadata["wasBot"], defaulting to false (human)
        // since most traffic is human
        var wasBot = evt.Label;
        if (!wasBot.HasValue && evt.Metadata?.TryGetValue("wasBot", out var wbObj) == true)
            wasBot = wbObj is bool wb ? wb : null;

        await _weightStore.RecordObservationAsync(
            sigType,
            evt.Pattern,
            wasBot ?? false, // Default to human if not specified
            evt.Confidence ?? 0.5,
            ct);

        _logger.LogDebug(
            "Signature feedback: {Type}/{Pattern}, wasBot={WasBot}",
            sigType, evt.Pattern, wasBot);
    }

    private async Task HandleUserFeedback(LearningEvent evt, CancellationToken ct)
    {
        // User feedback is highest quality - strong weight adjustment
        var wasBot = evt.Label ?? false;
        var wasCorrect = evt.Metadata?.TryGetValue("detection_correct", out var correct) == true
                         && correct is bool b && b;

        var signatures = ExtractSignatures(evt);

        foreach (var (sigType, signature) in signatures)
            // User feedback gets high confidence
            await _weightStore.UpdateWeightAsync(
                sigType,
                signature,
                wasBot ? 0.8 : -0.8, // Strong weight
                0.9, // High confidence from user feedback
                1,
                ct);

        _logger.LogInformation(
            "User feedback processed: wasBot={WasBot}, correct={Correct}, signatures={Count}",
            wasBot, wasCorrect, signatures.Count);
    }

    /// <summary>
    ///     Extract signatures from a learning event.
    /// </summary>
    private List<(string Type, string Signature)> ExtractSignatures(LearningEvent evt)
    {
        var signatures = new List<(string, string)>();

        // UA pattern signature
        if (evt.Metadata?.TryGetValue("userAgent", out var uaObj) == true &&
            uaObj is string userAgent && !string.IsNullOrEmpty(userAgent))
        {
            var uaPattern = NormalizeUserAgent(userAgent);
            signatures.Add((SignatureTypes.UaPattern, uaPattern));
        }

        // IP range signature
        if (evt.Metadata?.TryGetValue("ip", out var ipObj) == true &&
            ipObj is string ip && !string.IsNullOrEmpty(ip))
        {
            var ipRange = NormalizeIpToRange(ip);
            signatures.Add((SignatureTypes.IpRange, ipRange));
        }

        // Path pattern signature
        if (evt.Metadata?.TryGetValue("path", out var pathObj) == true &&
            pathObj is string path && !string.IsNullOrEmpty(path))
        {
            var pathPattern = NormalizePath(path);
            signatures.Add((SignatureTypes.PathPattern, pathPattern));
        }

        // Behavior hash signature
        if (evt.Features != null && evt.Features.Count > 0)
        {
            var behaviorHash = ComputeBehaviorHash(evt.Features);
            signatures.Add((SignatureTypes.BehaviorHash, behaviorHash));
        }

        // Combined signature (if we have enough data)
        if (signatures.Count >= 2)
        {
            var combined = ComputeCombinedSignature(signatures);
            signatures.Add((SignatureTypes.CombinedSignature, combined));
        }

        // Pattern from event
        if (!string.IsNullOrEmpty(evt.Pattern)) signatures.Add((SignatureTypes.CombinedSignature, evt.Pattern));

        return signatures;
    }

    /// <summary>
    ///     Normalize a User-Agent to a pattern suitable for matching.
    /// </summary>
    private static string NormalizeUserAgent(string userAgent)
    {
        // Extract key components: browser/version, OS, device type
        // Create a pattern that will match similar UAs

        var sb = new StringBuilder();

        // Browser family
        if (userAgent.Contains("Chrome/")) sb.Append("chrome:");
        else if (userAgent.Contains("Firefox/")) sb.Append("firefox:");
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome/")) sb.Append("safari:");
        else if (userAgent.Contains("Edg/")) sb.Append("edge:");
        else sb.Append("other:");

        // OS family
        if (userAgent.Contains("Windows")) sb.Append("win:");
        else if (userAgent.Contains("Mac OS")) sb.Append("mac:");
        else if (userAgent.Contains("Linux") && !userAgent.Contains("Android")) sb.Append("linux:");
        else if (userAgent.Contains("Android")) sb.Append("android:");
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad")) sb.Append("ios:");
        else sb.Append("unknown:");

        // Known bot indicators
        if (userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase))
            sb.Append("bot:");

        // Add length bucket for uniqueness
        sb.Append($"len{userAgent.Length / 50}");

        return sb.ToString();
    }

    /// <summary>
    ///     Normalize an IP to a range (CIDR block).
    /// </summary>
    private static string NormalizeIpToRange(string ip)
    {
        // For IPv4: use /24 (class C)
        // For IPv6: use /48

        if (ip.Contains('.'))
        {
            // IPv4 - take first 3 octets
            var parts = ip.Split('.');
            if (parts.Length >= 3) return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
        }
        else if (ip.Contains(':'))
        {
            // IPv6 - take first 3 segments
            var parts = ip.Split(':');
            if (parts.Length >= 3) return $"{parts[0]}:{parts[1]}:{parts[2]}::/48";
        }

        return ip; // Fallback to exact IP
    }

    /// <summary>
    ///     Normalize a path to a pattern.
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Remove query string
        var qIdx = path.IndexOf('?');
        if (qIdx >= 0) path = path[..qIdx];

        // Replace IDs with placeholders
        // e.g., /api/users/123 -> /api/users/{id}
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
            if (int.TryParse(parts[i], out _) ||
                Guid.TryParse(parts[i], out _))
                parts[i] = "{id}";

        return "/" + string.Join("/", parts);
    }

    /// <summary>
    ///     Compute a hash from behavioral features.
    /// </summary>
    private static string ComputeBehaviorHash(Dictionary<string, double> features)
    {
        // Take top features by absolute value
        var topFeatures = features
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(5)
            .Select(kv => $"{kv.Key}:{(int)(kv.Value * 100)}")
            .ToList();

        var combined = string.Join("|", topFeatures);
        return ComputeShortHash(combined);
    }

    /// <summary>
    ///     Compute a combined signature from multiple signatures.
    /// </summary>
    private static string ComputeCombinedSignature(List<(string Type, string Signature)> signatures)
    {
        var combined = string.Join("|", signatures.Take(3).Select(s => $"{s.Type}:{s.Signature}"));
        return ComputeShortHash(combined);
    }

    /// <summary>
    ///     Compute a short hash of a string.
    /// </summary>
    private static string ComputeShortHash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }
}