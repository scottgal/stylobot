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
        // ICE probe: "no srflx" is only meaningful when the probe ran
        // successfully (errored = 0). Errored means the API was unavailable
        // or threw -- treat as no-signal rather than as positive evidence.
        var iceProbe = data.IceProbe;
        bool? iceNoSrflx = iceProbe is { Errored: 0 }
            ? iceProbe.HasSrflx == 0
            : null;

        // TTS voice count: -1 is the sentinel for "speechSynthesis unsupported"
        // (older browsers, restricted contexts). Treat as no-signal.
        var ttsProbe = data.TtsProbe;
        int? ttsVoiceCount = ttsProbe?.VoiceCount switch
        {
            null or -1 => null,
            var n => n
        };

        // BotD verdict: only treat as positive evidence when bot=1 AND no error.
        // A null Kind on bot=1 is rare (BotD usually labels) but possible; we
        // surface "automated" as a generic catch-all kind.
        string? botdKind = data.Botd is { Bot: 1, Errored: 0 } botd
            ? (string.IsNullOrEmpty(botd.Kind) ? "automated" : botd.Kind)
            : null;

        // Mouse stats: pipe the count into the existing ClientMouseEvents
        // signal slot (until now a ghost signal nothing wrote to). Kameleo-
        // specific features (all-integer coords, timing CV) ride alongside.
        var mouse = data.Mouse;
        int? mouseMoves = mouse?.MoveCount;
        bool? mouseAllInt = mouse?.AllIntegerCoords switch
        {
            null or -1 => null,
            0 => false,
            _ => true
        };
        double? mouseCv = null;
        if (mouse is { DtMean: > 0, DtStddev: >= 0 })
            mouseCv = mouse.DtStddev / mouse.DtMean;

        var result = new BrowserFingerprintResult
        {
            RequestId = requestId,
            ProcessedAt = DateTimeOffset.UtcNow,
            // Adblocker probe payload is independent of the rest of the
            // fingerprint surface -- a probe-only beacon (when the main
            // fingerprint script was blocked) carries no other fields, just
            // adblocker:true + provider. Pass through verbatim; the contributor
            // translates this into the ClientSideAdblockerDetected signal.
            Adblocker = data.Adblocker,
            AdblockerProvider = data.AdblockerProvider,
            ConnectionType = data.Basics?.ConnectionType,
            IceNoSrflx = iceNoSrflx,
            TtsVoiceCount = ttsVoiceCount,
            BotdKind = botdKind,
            MouseMoveCount = mouseMoves,
            MouseAllIntegerCoords = mouseAllInt,
            MouseTimingCv = mouseCv,
        };

        // Handle error case. data.Error is attacker-controlled (JSON body);
        // truncate to a fixed cap so a malicious payload cannot grow log lines
        // or downstream rendering surfaces (dashboards iterate Reasons).
        if (!string.IsNullOrEmpty(data.Error))
        {
            const int maxErrorLen = 200;
            var truncated = data.Error.Length <= maxErrorLen
                ? data.Error
                : data.Error[..maxErrorLen] + "…";
            result.Reasons.Add($"Client-side collection error: {truncated}");
            result.BrowserIntegrityScore = 0;
            result.HeadlessLikelihood = 0.5; // Unknown
            result.FingerprintHash = "error";
            return result;
        }

        // Adblocker-only beacon: the probe TagHelper renders an inlined script
        // that beacons {adblocker: true, provider: ...} when the configured
        // ad-network resource was blocked. When the main fingerprint script was
        // ALSO blocked (no integrity / no plugin / no UA fields populated) we
        // get an adblocker-only payload. Recognise it by Adblocker=true AND a
        // zero Timestamp (the main script always stamps Date.now()). Don't run
        // integrity analysis -- zero-valued fingerprint fields would otherwise
        // look like a low-integrity bot. Produce a neutral result with the
        // adblocker flag set; ClientSideContributor reads it and writes the
        // suppression signal.
        if (data.Adblocker && data.Timestamp == 0)
        {
            result.Reasons.Add($"Adblocker probe blocked: provider={data.AdblockerProvider ?? "unknown"}");
            result.BrowserIntegrityScore = 50;   // neutral; the field wasn't analysed
            result.FingerprintConsistencyScore = 50;
            result.HeadlessLikelihood = 0;
            result.FingerprintHash = "adblocker-only";
            return result;
        }

        var reasons = new List<string>();
        var headlessScore = 0.0;
        var integrityDeductions = 0;

        // ===== Automation Detection =====
        // Order matters: check specific frameworks before falling back to generic WebDriver.
        // Each framework leaves distinct markers that let us name the exact tool.

        var tail = data.Tail;
        var headless = data.Headless;
        var basics = data.Basics;

        // FingerprintJS BotD verdict. BotD has its own marker dictionary
        // covering Selenium, Puppeteer, Playwright, PhantomJS, CefSharp,
        // Awesomium, Nightmare, plus ~40 distinctive-property fingerprints.
        // We trust it on positive (bot=1) verdicts; on bot=0 it carries no
        // information (could be a real human OR a sophisticated cloak that
        // BotD doesn't recognise -- our own probes pick those up). Confidence
        // is high because BotD's false-positive rate on real browsers is
        // documented as low and the framework name is informative.
        if (botdKind != null)
        {
            headlessScore += 0.5;
            integrityDeductions += 20;
            reasons.Add($"BotD verdict: {botdKind}");
            result.DetectedAutomation ??= botdKind;
        }

        // PhantomJS (window.callPhantom / window._phantom)
        if (tail?.Phantom == 1)
        {
            headlessScore += 0.5;
            reasons.Add("PhantomJS markers detected");
            result.DetectedAutomation ??= "PhantomJS";
        }

        // Selenium ('webdriver' in window or '__selenium_unwrapped' in window)
        if (tail?.Selenium == 1)
        {
            headlessScore += 0.5;
            reasons.Add("Selenium markers detected");
            result.DetectedAutomation ??= "Selenium";
        }

        // CDP getter trap (cdpHit): set when CDP read the console.debug property
        // descriptor while the script was loading. Init-time signal -- Puppeteer,
        // Playwright, Selenium, Bright Data all visible this way.
        if (data.CdpGetterHit == 1)
        {
            headlessScore += 0.4;
            reasons.Add("CDP getter trap on console.debug fired (init-time CDP read)");
            result.DetectedAutomation ??= "CDP/Puppeteer";
        }

        // CDP Runtime.enable probe (call-time): client called console.debug(probe)
        // where probe's toString() increments a counter. Chromium serialises
        // console.* args for the Console.messageAdded protocol event ONLY when
        // Runtime.enable is attached -- so a non-zero toString count is a
        // near-deterministic CDP-attached signal. Catches Bright Data Scraping
        // Browser (stock Chromium via remote CDP), damru's patched Playwright
        // during the Runtime.enable window, vanilla Puppeteer / Playwright.
        // -1 = probe errored; treat as no-signal.
        if (basics?.CdpRuntimeProbe > 0)
        {
            headlessScore += 0.4;
            integrityDeductions += 20;
            reasons.Add("CDP Runtime.enable attached (Console.messageAdded triggered probe serialisation)");
            result.DetectedAutomation ??= "CDP-attached browser";
        }

        // WebDriver flag (navigator.webdriver === true) - generic fallback with framework inference
        if (tail?.WebDriver == 1)
        {
            headlessScore += 0.5;
            reasons.Add("navigator.webdriver is true");

            // If no specific framework detected yet, infer from secondary signals
            if (result.DetectedAutomation == null)
            {
                if (tail.Selenium == 1)
                    result.DetectedAutomation = "Selenium";
                else if (data.CdpGetterHit == 1 || basics?.CdpRuntimeProbe > 0)
                    // CDP + webdriver = likely Puppeteer / Playwright
                    result.DetectedAutomation = "Puppeteer";
                else if (basics?.HasChromeObject == 1 && headless?.HasChromeRuntime == 0)
                    // window.chrome exists but chrome.runtime missing = likely Puppeteer
                    // (it strips runtime to look stealthy). Gated on hasChrome so Safari/
                    // Firefox with webdriver=1 don't mis-attribute to Puppeteer.
                    result.DetectedAutomation = "Puppeteer";
                else
                    result.DetectedAutomation = "WebDriver (unknown framework)";
            }
        }

        // ===== Browser Consistency Checks =====

        // window.chrome exists (Chrome family) but zero plugins is the canonical
        // headless / Puppeteer pattern. Gated on hasChrome so Safari and Firefox
        // (which report zero plugins because they have none) don't false-positive.
        if (basics?.HasChromeObject == 1 && headless?.PluginCount == 0)
        {
            headlessScore += 0.3;
            integrityDeductions += 20;
            reasons.Add("Chrome detected but no plugins present");
        }

        // Zero outer dimensions (headless indicator)
        if (basics != null && (basics.OuterWidth == 0 || basics.OuterHeight == 0))
        {
            headlessScore += 0.3;
            integrityDeductions += 30;
            reasons.Add("Window outer dimensions are zero");
        }

        // Inner equals outer (no browser chrome)
        if (basics != null &&
            basics.InnerWidth > 0 &&
            basics.InnerWidth == basics.OuterWidth &&
            basics.InnerHeight == basics.OuterHeight)
        {
            headlessScore += 0.15;
            integrityDeductions += 10;
            reasons.Add("Window has no browser chrome (inner == outer)");
        }

        // ===== Permission Consistency =====
        if (headless?.NotificationPermission == "suspicious")
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

        var platformLower = basics?.Platform?.ToLowerInvariant() ?? "";

        // No hardware concurrency (uncommon for real browsers)
        if (basics != null && basics.HardwareConcurrency == 0)
        {
            headlessScore += 0.1;
            integrityDeductions += 10;
            reasons.Add("Hardware concurrency not reported");
        }

        // No device memory (older API but still an indicator)
        if (basics != null && basics.DeviceMemory == 0
            && !platformLower.Contains("iphone") && !platformLower.Contains("ipad"))
            integrityDeductions += 5;

        // ===== Generate Fingerprint Hash =====
        result.FingerprintHash = GenerateFingerprintHash(data);
        result.ShapeHash = GenerateShapeHash(data);

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

    /// <summary>
    ///     Narrow "shape" hash: canvas + WebGL vendor + WebGL renderer. Excludes
    ///     the volatile fields (cores, memory, DPR, screen) that change per
    ///     session for the same visitor. Used as the lookup key in
    ///     <see cref="Identity.IFingerprintPoolCollisionTracker"/> so collisions
    ///     across distinct IP+session contexts surface Multilogin / Kameleo
    ///     curated-profile reuse.
    /// </summary>
    private static string? GenerateShapeHash(BrowserFingerprintData data)
    {
        var webgl = data.Webgl;
        var canvas = data.CanvasHash;
        if (string.IsNullOrEmpty(canvas) && webgl == null) return null;

        var components = new StringBuilder();
        components.Append(canvas ?? "");
        components.Append('|');
        components.Append(webgl?.Vendor ?? "");
        components.Append('|');
        components.Append(webgl?.Renderer ?? "");
        var bytes = Encoding.UTF8.GetBytes(components.ToString());
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GenerateFingerprintHash(BrowserFingerprintData data)
    {
        // Create a stable hash from key fingerprint components.
        var basics = data.Basics;
        var webgl = data.Webgl;
        var components = new StringBuilder();
        components.Append(basics?.Platform ?? "");
        components.Append('|');
        components.Append(basics?.ScreenResolution ?? "");
        components.Append('|');
        components.Append(basics?.Timezone ?? "");
        components.Append('|');
        components.Append(basics?.Language ?? "");
        components.Append('|');
        components.Append(basics?.HardwareConcurrency ?? 0);
        components.Append('|');
        components.Append(basics?.DevicePixelRatio ?? 0.0);
        components.Append('|');
        components.Append(webgl?.Vendor ?? "");
        components.Append('|');
        components.Append(webgl?.Renderer ?? "");
        components.Append('|');
        components.Append(data.CanvasHash ?? "");

        var bytes = Encoding.UTF8.GetBytes(components.ToString());
        var hash = XxHash64.Hash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int CalculateConsistencyScore(BrowserFingerprintData data)
    {
        var basics = data.Basics;
        if (basics == null) return 100;

        var score = 100;
        var platformLower = basics.Platform?.ToLowerInvariant() ?? "";

        // Check for inconsistencies

        // Mobile platform but large screen
        if ((platformLower.Contains("iphone") || platformLower.Contains("android")) &&
            !string.IsNullOrEmpty(basics.ScreenResolution))
        {
            var parts = basics.ScreenResolution.Split('x');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var width) &&
                width > 2000)
                score -= 20;
        }

        // Desktop platform but has touch
        var hasTouch = (data.Touch?.MaxTouchPoints ?? 0) > 0
                       || (data.Touch?.HasOnTouchStart ?? 0) == 1;
        if ((platformLower.Contains("win") || platformLower.Contains("mac") || platformLower.Contains("linux")) &&
            !platformLower.Contains("arm") &&
            hasTouch &&
            basics.DevicePixelRatio == 1)
            // Touch on desktop with DPR 1 is uncommon but not impossible
            score -= 5;

        // Missing timezone (very unusual)
        if (string.IsNullOrEmpty(basics.Timezone)) score -= 15;

        // Missing language (very unusual)
        if (string.IsNullOrEmpty(basics.Language)) score -= 15;

        // DPR of exactly 1 with high-res screen (unusual for modern displays)
        if (basics.DevicePixelRatio == 1 && !string.IsNullOrEmpty(basics.ScreenResolution))
        {
            var parts = basics.ScreenResolution.Split('x');
            if (parts.Length >= 2 &&
                int.TryParse(parts[0], out var width) &&
                width > 2560)
                score -= 10;
        }

        return Math.Max(0, score);
    }
}