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
        // Order matters: check specific frameworks before falling back to generic WebDriver.
        // Each framework leaves distinct markers that let us name the exact tool.

        // PhantomJS (window.callPhantom / window._phantom)
        if (data.Phantom == 1)
        {
            headlessScore += 0.5;
            reasons.Add("PhantomJS markers detected");
            result.DetectedAutomation = "PhantomJS";
        }

        // Nightmare.js (window.__nightmare)
        if (data.Nightmare)
        {
            headlessScore += 0.5;
            reasons.Add("Nightmare.js markers detected");
            result.DetectedAutomation ??= "Nightmare";
        }

        // Selenium (document.$cdc_ / document.$wdc_ / __selenium_unwrapped)
        if (data.Selenium)
        {
            headlessScore += 0.5;
            reasons.Add("Selenium markers detected");
            result.DetectedAutomation ??= "Selenium";
        }

        // Playwright (__playwright / __pw_* globals)
        if (data.Playwright == 1)
        {
            headlessScore += 0.5;
            reasons.Add("Playwright markers detected");
            result.DetectedAutomation ??= "Playwright";
        }

        // Chrome DevTools Protocol (CDP markers - Puppeteer or other CDP-based tools)
        if (data.ChromeDevTools == 1)
        {
            headlessScore += 0.4;
            reasons.Add("Chrome DevTools Protocol markers detected");
            result.DetectedAutomation ??= "CDP/Puppeteer";
        }

        // WebDriver flag (navigator.webdriver = true) - generic fallback with framework inference
        if (data.WebDriver == 1)
        {
            headlessScore += 0.5;
            reasons.Add("navigator.webdriver is true");

            // If no specific framework detected yet, infer from secondary signals
            if (result.DetectedAutomation == null)
            {
                if (data.Playwright == 1)
                    result.DetectedAutomation = "Playwright";
                else if (data.Selenium)
                    result.DetectedAutomation = "Selenium";
                else if (data.ChromeDevTools == 1)
                    // CDP + webdriver + no chrome.runtime = likely Puppeteer
                    result.DetectedAutomation = "Puppeteer";
                else if (data.HasChromeObject && data.HasChromeRuntime == 0)
                    // Chrome without chrome.runtime = likely Puppeteer (it removes runtime)
                    result.DetectedAutomation = "Puppeteer";
                else
                    result.DetectedAutomation = "WebDriver (unknown framework)";
            }
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

        // ===== JS Execution Timing Analysis =====
        // Detects headless browsers (Puppeteer stealth, Playwright) that pass static checks
        // but have different timing characteristics due to lack of real rendering pipeline

        if (data.LayoutTimeMs.HasValue)
        {
            var layoutTime = data.LayoutTimeMs.Value;
            // Instant layout = no real rendering pipeline (headless)
            if (layoutTime < 0.5)
            {
                headlessScore += 0.25;
                integrityDeductions += 20;
                reasons.Add($"Instant DOM layout ({layoutTime:F3}ms) - no rendering pipeline");
            }
            // Artificially slow layout = delay injection to evade timing detection
            else if (layoutTime > 50)
            {
                headlessScore += 0.15;
                integrityDeductions += 10;
                reasons.Add($"Suspiciously slow DOM layout ({layoutTime:F1}ms) - possible delay injection");
            }
        }

        if (data.SetTimeoutDrift.HasValue)
        {
            var drift = data.SetTimeoutDrift.Value;
            // Near-zero setTimeout drift = headless environments bypass timer coalescing
            if (drift < 0.5)
            {
                headlessScore += 0.2;
                integrityDeductions += 15;
                reasons.Add($"Near-zero setTimeout drift ({drift:F3}ms) - timer coalescing absent");
            }
            // Excessively high drift = deliberately throttled to appear human
            else if (drift > 50)
            {
                headlessScore += 0.1;
                integrityDeductions += 10;
                reasons.Add($"Excessively high setTimeout drift ({drift:F1}ms) - possible throttling");
            }
        }

        if (data.PerformanceResolution.HasValue)
        {
            var resolution = data.PerformanceResolution.Value;
            // Post-Spectre reduced resolution (> 100us) - most real browsers have this
            // but some headless configs use non-standard values
            if (resolution > 0.1)
            {
                headlessScore += 0.1;
                integrityDeductions += 10;
                reasons.Add($"Coarse performance.now() resolution ({resolution:F4}ms)");
            }
            // Unrealistically fine resolution (< 1us) - unpatched or non-standard environment
            else if (resolution < 0.001 && resolution > 0)
            {
                headlessScore += 0.15;
                integrityDeductions += 15;
                reasons.Add($"Unrealistically fine performance.now() resolution ({resolution:F6}ms)");
            }
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

        // ===== Populate timing probe results =====
        result.LayoutTimeMs = data.LayoutTimeMs;
        result.SetTimeoutDrift = data.SetTimeoutDrift;
        result.PerformanceResolution = data.PerformanceResolution;
        result.TimingAnomaly = reasons.Exists(r =>
            r.Contains("layout", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("setTimeout", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("performance.now()", StringComparison.OrdinalIgnoreCase));

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