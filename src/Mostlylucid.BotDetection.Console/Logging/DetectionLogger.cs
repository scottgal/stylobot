using System.Text;
using Mostlylucid.BotDetection.Console.Extensions;
using Mostlylucid.BotDetection.Console.Helpers;
using Mostlylucid.BotDetection.Console.Models;
using Mostlylucid.BotDetection.Models;
using Serilog;

namespace Mostlylucid.BotDetection.Console.Logging;

/// <summary>
///     Handles bot detection result logging for both demo and production modes
/// </summary>
public static class DetectionLogger
{
    /// <summary>
    ///     Demo mode: Full verbose logging with all signals
    /// </summary>
    public static void LogDetectionDemo(
        HttpContext context,
        BotDetectionResult detection,
        TimeSpan elapsed,
        SignatureLoggingConfig config)
    {
        var ctx = PrepareDetectionContext(context, config);

        // Check if request was blocked
        var wasBlocked = context.WasBlocked();
        var actionTaken = context.BotDetectionAction();

        if (wasBlocked)
        {
            // BIG RED BLOCKED BANNER for demo mode
            Log.Error("╔════════════════════════════════════════════════════════════╗");
            Log.Error("║                   🚫 REQUEST BLOCKED 🚫                    ║");
            Log.Error("╚════════════════════════════════════════════════════════════╝");
        }
        else
        {
            Log.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Log.Information("🔍 Bot Detection Result");
            Log.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }

        Log.Information("  Request:     {Method} {Path}", context.Request.Method, context.Request.Path);
        Log.Information("  IP:          {IP}", ctx.IpDisplay);
        Log.Information("  User-Agent:  {UA}", ctx.UaDisplay);
        if (!string.IsNullOrEmpty(ctx.PiiWarning)) Log.Warning(ctx.PiiWarning);
        Log.Information("");

        // Determine bot status with confidence level
        var botStatus = detection.IsBot
            ? detection.ConfidenceScore >= 0.8 ? "✗ YES (High Confidence)"
            : detection.ConfidenceScore >= 0.6 ? "✗ LIKELY (Medium Confidence)"
            : "✗ MAYBE (Low Confidence)"
            : "✓ NO";

        // Show bot type - if detected as bot but no specific type, show "Unknown/Automated"
        var botTypeDisplay = detection.BotType?.ToString()
                             ?? (detection.IsBot ? "Unknown/Automated" : "N/A");

        Log.Information("  IsBot:       {IsBot}", botStatus);
        Log.Information("  Confidence:  {Confidence:F2}", detection.ConfidenceScore);
        Log.Information("  Bot Type:    {BotType}", botTypeDisplay);
        Log.Information("  Bot Name:    {BotName}", detection.BotName ?? "N/A");
        Log.Information("  Time:        {Time:F2}ms", elapsed.TotalMilliseconds);
        if (wasBlocked) Log.Error("  ⚠️ ACTION:     {Action}", actionTaken);
        Log.Information("");

        if (detection.Reasons != null && detection.Reasons.Count > 0)
        {
            Log.Information("  Detection Reasons: {Count}", detection.Reasons.Count);
            Log.Information("  ┌─────────────────────────────────────────────────────");
            foreach (var reason in detection.Reasons.OrderByDescending(r => r.ConfidenceImpact))
                Log.Information("  │ {Category,-25} {Impact,6:F2} - {Detail}",
                    reason.Category,
                    reason.ConfidenceImpact,
                    reason.Detail.Length > 40 ? reason.Detail.Substring(0, 37) + "..." : reason.Detail);
            Log.Information("  └─────────────────────────────────────────────────────");
        }

        // Show additional detection metadata
        var category = context.BotDetectionCategory();
        if (category != null) Log.Information("  Primary Category: {Category}", category);

        var policy = context.BotDetectionPolicy();
        if (policy != null) Log.Information("  Policy Used:      {Policy}", policy);

        if (wasBlocked)
            Log.Error("╚════════════════════════════════════════════════════════════╝");
        else
            Log.Information("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Log.Information("");
    }

    /// <summary>
    ///     Production mode: Concise logging (ALWAYS zero-PII)
    /// </summary>
    public static void LogDetectionProduction(
        HttpContext context,
        BotDetectionResult detection,
        TimeSpan elapsed,
        SignatureLoggingConfig config)
    {
        var result = detection.IsBot ? "BOT" : "HUMAN";
        var symbol = detection.IsBot ? "✗" : "✓";
        var botType = detection.BotType?.ToString() ?? "-";
        var botName = detection.BotName ?? "-";

        // ALWAYS use HMAC hash in production (zero-PII)
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var keyBytes = Encoding.UTF8.GetBytes(config.SignatureHashKey);
        var ipHash = HmacHelper.ComputeHmacHash(keyBytes, ip);

        Log.Information(
            "{Symbol} {Result,-6} {Confidence,4:F2} {BotType,-15} {Time,5:F0}ms {Method,-4} {Path} [{IpHash}] {BotName}",
            symbol,
            result,
            detection.ConfidenceScore,
            botType,
            elapsed.TotalMilliseconds,
            context.Request.Method,
            context.Request.Path,
            ipHash,
            botName);
    }

    /// <summary>
    ///     Prepare detection context with HMAC hashes and PII handling
    /// </summary>
    private static DetectionContext PrepareDetectionContext(HttpContext context, SignatureLoggingConfig config)
    {
        var ua = context.Request.Headers.UserAgent.ToString();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Compute HMAC hashes for zero-PII logging (default)
        var keyBytes = Encoding.UTF8.GetBytes(config.SignatureHashKey);
        var ipHash = HmacHelper.ComputeHmacHash(keyBytes, ip);
        var uaHash = HmacHelper.ComputeHmacHash(keyBytes, ua);

        // Format display strings based on LogRawPii setting
        string ipDisplay, uaDisplay, piiWarning = "";

        if (config.LogRawPii)
        {
            // Demo mode with explicit PII override: show raw + hash
            ipDisplay = $"{ip} (hash: {ipHash})";
            var uaTruncated = ua.Length > 40 ? ua.Substring(0, 37) + "..." : ua;
            uaDisplay = $"{uaTruncated} (hash: {uaHash})";
            piiWarning =
                "  ⚠️  PII LOGGING ENABLED (disabled by default in production - configure via SignatureLogging:LogRawPii)";
        }
        else
        {
            // DEFAULT: Zero-PII mode (hash only)
            ipDisplay = ipHash;
            uaDisplay = uaHash;
        }

        return new DetectionContext(ip, ipHash, ua, uaHash, ipDisplay, uaDisplay, piiWarning);
    }

    /// <summary>
    ///     Context for detection logging (extracted from HttpContext + config)
    /// </summary>
    private record DetectionContext(
        string Ip,
        string IpHash,
        string Ua,
        string UaHash,
        string IpDisplay,
        string UaDisplay,
        string PiiWarning);
}