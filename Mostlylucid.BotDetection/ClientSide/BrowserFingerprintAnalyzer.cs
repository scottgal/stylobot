using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Analyzes browser fingerprint data to detect automation and headless browsers.
/// </summary>
public interface IBrowserFingerprintAnalyzer
{
    /// <summary>
    ///     Analyzes fingerprint data and produces a result with scores and reasons.
    /// </summary>
    BrowserFingerprintResult Analyze(BrowserFingerprintData data, string requestId);
}

public class BrowserFingerprintAnalyzer : IBrowserFingerprintAnalyzer
{
    private readonly ILogger<BrowserFingerprintAnalyzer> _logger;

    public BrowserFingerprintAnalyzer(ILogger<BrowserFingerprintAnalyzer> logger)
    {
        _logger = logger;
    }

    public BrowserFingerprintResult Analyze(BrowserFingerprintData data, string requestId)
    {
        var result = new BrowserFingerprintResult
        {
            RequestId = requestId,
            ProcessedAt = DateTimeOffset.UtcNow
        };

        // Handle error case
        if (!string.IsNullOrEmpty(data.Error))
        {
            result.Reasons.Add($"Client-side collection error: {data.Error}");
            result.BrowserIntegrityScore = 0;
            result.HeadlessLikelihood = 0.5; // Unknown
            result.FingerprintHash = "error";
            return result;
        }

        var reasons = new List<string>();
        var headlessScore = 0.0;
        var integrityDeductions = 0;

        // ===== Automation Detection =====

        // WebDriver flag (definitive)
        if (data.WebDriver == 1)
        {
            headlessScore += 0.5;
            reasons.Add("navigator.webdriver is true");
            result.DetectedAutomation = "WebDriver";
        }

        // PhantomJS
        if (data.Phantom == 1)
        {
            headlessScore += 0.5;
            reasons.Add("PhantomJS markers detected");
            result.DetectedAutomation ??= "PhantomJS";
        }

        // Nightmare
        if (data.Nightmare)
        {
            headlessScore += 0.5;
            reasons.Add("Nightmare.js markers detected");
            result.DetectedAutomation ??= "Nightmare";
        }

        // Selenium
        if (data.Selenium)
        {
            headlessScore += 0.5;
            reasons.Add("Selenium markers detected");
            result.DetectedAutomation ??= "Selenium";
        }

        // Chrome DevTools Protocol
        if (data.ChromeDevTools == 1)
        {
            headlessScore += 0.4;
            reasons.Add("Chrome DevTools Protocol markers detected");
            result.DetectedAutomation ??= "CDP/Puppeteer";
        }

        // ===== Browser Consistency Checks =====

        // Chrome without plugins (highly suspicious)
        if (data.HasChromeObject && data.PluginCount == 0)
        {
            headlessScore += 0.3;
            integrityDeductions += 20;
            reasons.Add("Chrome detected but no plugins present");
        }

        // Zero outer dimensions (headless indicator)
        if (data.OuterWidth == 0 || data.OuterHeight == 0)
        {
            headlessScore += 0.3;
            integrityDeductions += 30;
            reasons.Add("Window outer dimensions are zero");
        }

        // Inner equals outer (no browser chrome)
        if (data.InnerWidth > 0 &&
            data.InnerWidth == data.OuterWidth &&
            data.InnerHeight == data.OuterHeight)
        {
            headlessScore += 0.15;
            integrityDeductions += 10;
            reasons.Add("Window has no browser chrome (inner == outer)");
        }

        // ===== Function Integrity =====

        // Non-native bind function (prototype pollution)
        if (data.BindIsNative == 0)
        {
            headlessScore += 0.2;
            integrityDeductions += 20;
            reasons.Add("Function.prototype.bind is not native");
        }

        // Suspicious eval length (normal is ~33-37 chars)
        if (data.EvalLength > 0 && (data.EvalLength < 30 || data.EvalLength > 50))
        {
            headlessScore += 0.15;
            integrityDeductions += 15;
            reasons.Add($"Suspicious eval.toString() length: {data.EvalLength}");
        }

        // ===== Permission Consistency =====
        if (data.NotificationPermission == "suspicious")
        {
            headlessScore += 0.25;
            integrityDeductions += 25;
            reasons.Add("Permission state inconsistent with plugin count");
        }

        // ===== Platform Consistency =====

        // Check platform vs other signals
        var platformLower = data.Platform?.ToLowerInvariant() ?? "";

        // No hardware concurrency (uncommon for real browsers)
        if (data.HardwareConcurrency == 0)
        {
            headlessScore += 0.1;
            integrityDeductions += 10;
            reasons.Add("Hardware concurrency not reported");
        }

        // No device memory (older API but still an indicator)
        if (data.DeviceMemory == 0 && !platformLower.Contains("iphone") && !platformLower.Contains("ipad"))
            integrityDeductions += 5;

        // ===== Generate Fingerprint Hash =====
        result.FingerprintHash = GenerateFingerprintHash(data);

        // ===== Calculate Final Scores =====
        result.HeadlessLikelihood = Math.Min(1.0, headlessScore);
        result.IsHeadless = result.HeadlessLikelihood >= 0.5;
        result.BrowserIntegrityScore = Math.Max(0, 100 - integrityDeductions);
        result.FingerprintConsistencyScore = CalculateConsistencyScore(data);
        result.Reasons = reasons;

        _logger.LogDebug(
            "Fingerprint analysis: HeadlessLikelihood={Headless:F2}, IntegrityScore={Integrity}, " +
            "ConsistencyScore={Consistency}, Automation={Automation}",
            result.HeadlessLikelihood, result.BrowserIntegrityScore,
            result.FingerprintConsistencyScore, result.DetectedAutomation ?? "none");

        return result;
    }

    private static string GenerateFingerprintHash(BrowserFingerprintData data)
    {
        // Create a stable hash from key fingerprint components
        var components = new StringBuilder();
        components.Append(data.Platform ?? "");
        components.Append('|');
        components.Append(data.ScreenResolution ?? "");
        components.Append('|');
        components.Append(data.Timezone ?? "");
        components.Append('|');
        components.Append(data.Language ?? "");
        components.Append('|');
        components.Append(data.HardwareConcurrency);
        components.Append('|');
        components.Append(data.DevicePixelRatio);
        components.Append('|');
        components.Append(data.WebGLVendor ?? "");
        components.Append('|');
        components.Append(data.WebGLRenderer ?? "");
        components.Append('|');
        components.Append(data.CanvasHash ?? "");

        var bytes = Encoding.UTF8.GetBytes(components.ToString());
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int CalculateConsistencyScore(BrowserFingerprintData data)
    {
        var score = 100;
        var platformLower = data.Platform?.ToLowerInvariant() ?? "";

        // Check for inconsistencies

        // Mobile platform but large screen
        if ((platformLower.Contains("iphone") || platformLower.Contains("android")) &&
            !string.IsNullOrEmpty(data.ScreenResolution))
        {
            var parts = data.ScreenResolution.Split('x');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var width) &&
                width > 2000)
                score -= 20;
        }

        // Desktop platform but has touch
        if ((platformLower.Contains("win") || platformLower.Contains("mac") || platformLower.Contains("linux")) &&
            !platformLower.Contains("arm") &&
            data.HasTouch == 1 &&
            data.DevicePixelRatio == 1)
            // Touch on desktop with DPR 1 is uncommon but not impossible
            score -= 5;

        // Missing timezone (very unusual)
        if (string.IsNullOrEmpty(data.Timezone)) score -= 15;

        // Missing language (very unusual)
        if (string.IsNullOrEmpty(data.Language)) score -= 15;

        // DPR of exactly 1 with high-res screen (unusual for modern displays)
        if (data.DevicePixelRatio == 1 &&
            !string.IsNullOrEmpty(data.ScreenResolution))
        {
            var parts = data.ScreenResolution.Split('x');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var width) &&
                width > 2560)
                score -= 10;
        }

        return Math.Max(0, score);
    }
}