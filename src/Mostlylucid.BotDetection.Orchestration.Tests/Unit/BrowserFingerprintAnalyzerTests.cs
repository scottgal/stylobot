using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.ClientSide;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Comprehensive tests for the BrowserFingerprintAnalyzer. Exercises the
///     nested-block DTO that mirrors the actual JS payload shape emitted by
///     <c>botdetection.js</c>.
/// </summary>
public class BrowserFingerprintAnalyzerTests
{
    private readonly BrowserFingerprintAnalyzer _analyzer;

    public BrowserFingerprintAnalyzerTests()
    {
        _analyzer = new BrowserFingerprintAnalyzer(NullLogger<BrowserFingerprintAnalyzer>.Instance);
    }

    #region Permission Consistency

    [Fact]
    public void Analyze_SuspiciousPermissions_Flagged()
    {
        var data = CreateRealChromeBrowserData();
        data.Headless!.NotificationPermission = "suspicious";

        var result = _analyzer.Analyze(data, "test-request-17");

        Assert.True(result.HeadlessLikelihood >= 0.25);
        Assert.Contains(result.Reasons, r => r.Contains("Permission"));
    }

    [Fact]
    public void Analyze_CdpRuntimeProbeFired_FlagsRuntimeAttached()
    {
        // Realistic Chrome fingerprint, but the client-side probe observed
        // console.debug(probe) triggering toString() -- a near-deterministic
        // signal that CDP Runtime.enable is attached (Bright Data, Puppeteer,
        // damru's patched Playwright).
        var data = CreateRealChromeBrowserData();
        data.Basics!.CdpRuntimeProbe = 1;

        var result = _analyzer.Analyze(data, "test-cdp-probe");

        Assert.True(result.HeadlessLikelihood >= 0.3,
            $"Expected HeadlessLikelihood >= 0.3 for CDP Runtime.enable signal, got {result.HeadlessLikelihood}");
        Assert.Contains(result.Reasons,
            r => r.Contains("CDP", StringComparison.OrdinalIgnoreCase)
                 && r.Contains("Runtime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_CdpRuntimeProbeZero_NoCdpReason()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.CdpRuntimeProbe = 0;

        var result = _analyzer.Analyze(data, "test-no-cdp");

        Assert.DoesNotContain(result.Reasons,
            r => r.Contains("CDP", StringComparison.OrdinalIgnoreCase)
                 && r.Contains("Runtime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_CdpRuntimeProbeErrored_NoCdpReason()
    {
        // -1 sentinel = probe errored client-side; treat as unknown, not as attack.
        var data = CreateRealChromeBrowserData();
        data.Basics!.CdpRuntimeProbe = -1;

        var result = _analyzer.Analyze(data, "test-cdp-errored");

        Assert.DoesNotContain(result.Reasons,
            r => r.Contains("CDP", StringComparison.OrdinalIgnoreCase)
                 && r.Contains("Runtime", StringComparison.OrdinalIgnoreCase));
    }

    // === Production wire-format round-trip =================================
    // botdetection.js emits a nested payload: { tail: { webdriver: 1 }, ... }.
    // These tests deserialise that exact shape through System.Text.Json and
    // assert the analyser flags it. Catches any reintroduction of the silent
    // JS-to-DTO mismatch that left every nested signal arriving as a default
    // value before the nested-block refactor.

    [Fact]
    public void Analyze_ProductionPayloadShape_DetectsWebDriverFromNestedTail()
    {
        const string json = """
            {
              "t": "token",
              "v": "2.0.0",
              "ts": 1700000000000,
              "cdp": 0,
              "interacted": 0,
              "basics": {
                "tz": "America/New_York",
                "lang": "en-US",
                "langs": "en-US,en",
                "platform": "Win32",
                "cores": 8,
                "mem": 8,
                "screen": "1920x1080x24",
                "avail": "1920x1040",
                "dpr": 1.25,
                "outerW": 1920,
                "outerH": 1040,
                "innerW": 1903,
                "innerH": 969,
                "dark": 0,
                "reduced": 0,
                "net": "4g",
                "hasChrome": 1,
                "connType": "",
                "cdpRuntime": 0
              },
              "tail":     { "webdriver": 1, "phantom": 0, "selenium": 0, "iframe": 0 },
              "headless": { "plugins": 5, "notif": "default", "chromeRt": 1, "languages": 2, "connection": 1 },
              "touch":    { "maxTouch": 0, "ontouch": 0, "pointerEvt": 1 },
              "stack":    { "len": 100, "hasObjectApply": 0, "hasAnonymous": 0 },
              "triple":   { "viewTx": 1, "speculation": 1, "storageAccess": 1 },
              "legit":    { "brave": 0, "lockdown": 0 }
            }
            """;
        var data = JsonSerializer.Deserialize<BrowserFingerprintData>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);

        var result = _analyzer.Analyze(data!, "test-prod-shape-webdriver");

        Assert.Contains(result.Reasons,
            r => r.Contains("webdriver", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_ProductionPayloadShape_PipesIceAndTtsThroughToResult()
    {
        // Wire format: ice + tts are top-level objects in the JS payload.
        // Asserts both blocks deserialise and the analyser exposes
        // IceNoSrflx + TtsVoiceCount on the result for downstream consumers.
        const string json = """
            {
              "t": "token", "v": "2.0.0", "ts": 1700000000000, "cdp": 0, "interacted": 0,
              "basics":   { "platform": "Linux armv8l", "hasChrome": 1, "cores": 8,
                            "screen": "412x915x24", "dpr": 2.625,
                            "outerW": 412, "outerH": 915, "innerW": 412, "innerH": 819,
                            "tz": "Asia/Tokyo", "lang": "ja-JP" },
              "tail":     { "webdriver": 0, "phantom": 0, "selenium": 0, "iframe": 0 },
              "headless": { "plugins": 0, "notif": "default", "chromeRt": 1 },
              "touch":    { "maxTouch": 5, "ontouch": 1, "pointerEvt": 1 },
              "ice":      { "count": 1, "srflx": 0, "host": 1, "errored": 0 },
              "tts":      { "count": 0 }
            }
            """;
        var data = JsonSerializer.Deserialize<BrowserFingerprintData>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);
        Assert.NotNull(data!.IceProbe);
        Assert.NotNull(data.TtsProbe);

        var result = _analyzer.Analyze(data, "test-prod-shape-ice-tts");

        Assert.True(result.IceNoSrflx);
        Assert.Equal(0, result.TtsVoiceCount);
    }

    [Fact]
    public void Analyze_ProductionPayloadShape_FlagsCdpRuntimeFromNestedBasics()
    {
        const string json = """
            {
              "t": "token", "v": "2.0.0", "ts": 1700000000000, "cdp": 0, "interacted": 0,
              "basics":   { "tz": "America/New_York", "lang": "en-US", "platform": "Win32",
                            "cores": 8, "mem": 8, "screen": "1920x1080x24", "dpr": 1.25,
                            "outerW": 1920, "outerH": 1040, "innerW": 1903, "innerH": 969,
                            "hasChrome": 1, "connType": "", "cdpRuntime": 1 },
              "tail":     { "webdriver": 0, "phantom": 0, "selenium": 0, "iframe": 0 },
              "headless": { "plugins": 5, "notif": "default", "chromeRt": 1 },
              "touch":    { "maxTouch": 0 },
              "stack":    { "len": 100 },
              "triple":   { "viewTx": 1 },
              "legit":    { "brave": 0, "lockdown": 0 }
            }
            """;
        var data = JsonSerializer.Deserialize<BrowserFingerprintData>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(data);

        var result = _analyzer.Analyze(data!, "test-prod-shape-cdp");

        Assert.Contains(result.Reasons,
            r => r.Contains("CDP", StringComparison.OrdinalIgnoreCase)
                 && r.Contains("Runtime", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    ///     Builds a BrowserFingerprintData populated with the values a normal,
    ///     fully-featured Chrome installation would emit through
    ///     <c>botdetection.js</c>. Used as the baseline; individual tests
    ///     mutate one or two fields and re-run analysis.
    /// </summary>
    private static BrowserFingerprintData CreateRealChromeBrowserData() => new()
    {
        Timestamp = 1700000000000,
        CdpGetterHit = 0,
        Interacted = 1,
        Basics = new BasicsBlock
        {
            Timezone = "America/New_York",
            Language = "en-US",
            Languages = "en-US,en",
            Platform = "Win32",
            HardwareConcurrency = 8,
            DeviceMemory = 8,
            ScreenResolution = "1920x1080x24",
            AvailableResolution = "1920x1040",
            DevicePixelRatio = 1.25,
            OuterWidth = 1920,
            OuterHeight = 1040,
            InnerWidth = 1903,
            InnerHeight = 969,
            DarkMode = 0,
            ReducedMotion = 0,
            NetworkEffectiveType = "4g",
            HasChromeObject = 1,
            ConnectionType = "",
            CdpRuntimeProbe = 0
        },
        Tail = new TailBlock { WebDriver = 0, Phantom = 0, Selenium = 0, Iframe = 0 },
        Headless = new HeadlessBlock
        {
            PluginCount = 5,
            NotificationPermission = "default",
            HasChromeRuntime = 1,
            LanguageCount = 2,
            HasConnectionApi = 1
        },
        Touch = new TouchBlock { MaxTouchPoints = 0, HasOnTouchStart = 0, HasPointerEvent = 1 },
        Stack = new StackBlock { Length = 100, HasObjectApply = 0, HasAnonymous = 0 },
        Triple = new TripleBlock { HasViewTransition = 1, HasSpeculationRules = 1, HasStorageAccess = 1 },
        Legit = new LegitBlock { Brave = 0, Lockdown = 0 },
        Webgl = new WebglBlock { Vendor = "Google Inc.", Renderer = "ANGLE (Intel)" },
        CanvasHash = "abc123def456"
    };

    #endregion

    #region Real Browser Detection

    [Fact]
    public void Analyze_RealChromeBrowser_ReturnsHighIntegrity()
    {
        var data = CreateRealChromeBrowserData();

        var result = _analyzer.Analyze(data, "test-request-1");

        Assert.False(result.IsHeadless);
        Assert.True(result.HeadlessLikelihood < 0.3, $"HeadlessLikelihood {result.HeadlessLikelihood} should be < 0.3");
        Assert.True(result.BrowserIntegrityScore >= 80,
            $"IntegrityScore {result.BrowserIntegrityScore} should be >= 80");
        Assert.Null(result.DetectedAutomation);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Analyze_RealFirefoxBrowser_ReturnsHighIntegrity()
    {
        // Firefox doesn't expose window.chrome
        var data = CreateRealChromeBrowserData();
        data.Basics!.HasChromeObject = 0;
        data.Headless!.PluginCount = 3; // Firefox has fewer plugins typically

        var result = _analyzer.Analyze(data, "test-request-2");

        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 80);
        Assert.Null(result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_RealSafariBrowser_ReturnsHighIntegrity()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.Platform = "MacIntel";
        data.Basics.HasChromeObject = 0;
        data.Basics.DevicePixelRatio = 2; // Retina display
        data.Headless!.PluginCount = 2;

        var result = _analyzer.Analyze(data, "test-request-3");

        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 80);
    }

    [Fact]
    public void Analyze_RealMobileBrowser_ReturnsHighIntegrity()
    {
        // iPhone Safari: no window.chrome, no plugins, no chrome.runtime, no
        // device memory API. All of those are *correct* defaults for iOS Safari
        // and must NOT trigger headless-Chrome flags.
        var data = new BrowserFingerprintData
        {
            Timestamp = 1700000000000,
            Basics = new BasicsBlock
            {
                Timezone = "America/New_York",
                Language = "en-US",
                Languages = "en-US,en",
                Platform = "iPhone",
                HardwareConcurrency = 6,
                DeviceMemory = 0,
                ScreenResolution = "390x844x24",
                DevicePixelRatio = 3,
                OuterWidth = 390,
                OuterHeight = 844,
                InnerWidth = 390,
                InnerHeight = 664,
                HasChromeObject = 0
            },
            Tail = new TailBlock { WebDriver = 0, Phantom = 0, Selenium = 0, Iframe = 0 },
            Headless = new HeadlessBlock
            {
                PluginCount = 0,
                NotificationPermission = "default",
                HasChromeRuntime = 0
            },
            Touch = new TouchBlock { MaxTouchPoints = 5, HasOnTouchStart = 1, HasPointerEvent = 1 }
        };

        var result = _analyzer.Analyze(data, "test-request-4");

        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 70);
    }

    #endregion

    #region WebDriver Detection

    [Fact]
    public void Analyze_WebDriverTrue_DetectsAsHeadless()
    {
        var data = CreateRealChromeBrowserData();
        data.Tail!.WebDriver = 1;

        var result = _analyzer.Analyze(data, "test-request-5");

        Assert.True(result.IsHeadless);
        Assert.True(result.HeadlessLikelihood >= 0.5);
        Assert.NotNull(result.DetectedAutomation);
        Assert.True(result.DetectedAutomation is "Puppeteer" or "Playwright" or "Selenium" or "WebDriver" or "CDP/Puppeteer"
            || result.DetectedAutomation.Contains("WebDriver"),
            $"Expected a known automation framework but got: {result.DetectedAutomation}");
        Assert.Contains(result.Reasons, r => r.Contains("webdriver"));
    }

    [Fact]
    public void Analyze_WebDriverWithRealBrowserData_StillDetectsBot()
    {
        // Sophisticated bot tries to look real but can't hide webdriver
        var data = CreateRealChromeBrowserData();
        data.Tail!.WebDriver = 1;

        var result = _analyzer.Analyze(data, "test-request-6");

        Assert.True(result.IsHeadless);
        Assert.NotNull(result.DetectedAutomation);
    }

    #endregion

    #region Automation Framework Detection

    [Fact]
    public void Analyze_PhantomJS_DetectsAsHeadless()
    {
        var data = CreateRealChromeBrowserData();
        data.Tail!.Phantom = 1;

        var result = _analyzer.Analyze(data, "test-request-7");

        Assert.True(result.IsHeadless);
        Assert.Equal("PhantomJS", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("PhantomJS"));
    }

    [Fact]
    public void Analyze_Selenium_DetectsAsHeadless()
    {
        var data = CreateRealChromeBrowserData();
        data.Tail!.Selenium = 1;

        var result = _analyzer.Analyze(data, "test-request-9");

        Assert.True(result.IsHeadless);
        Assert.Equal("Selenium", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("Selenium"));
    }

    [Fact]
    public void Analyze_CdpGetterTrapHit_DetectsAsHeadless()
    {
        // Init-time CDP detection via the getter trap on console.debug.
        var data = CreateRealChromeBrowserData();
        data.CdpGetterHit = 1;

        var result = _analyzer.Analyze(data, "test-request-10");

        Assert.True(result.HeadlessLikelihood >= 0.4);
        Assert.Equal("CDP/Puppeteer", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("CDP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_MultipleAutomationMarkers_DetectsFirst()
    {
        var data = CreateRealChromeBrowserData();
        data.Tail!.WebDriver = 1;
        data.Tail.Selenium = 1;
        data.CdpGetterHit = 1;

        var result = _analyzer.Analyze(data, "test-request-11");

        Assert.True(result.IsHeadless);
        Assert.NotNull(result.DetectedAutomation);
        Assert.True(result.DetectedAutomation is "Puppeteer" or "Playwright" or "Selenium" or "WebDriver" or "CDP/Puppeteer"
            || result.DetectedAutomation.Contains("WebDriver"),
            $"Expected a known automation framework but got: {result.DetectedAutomation}");
        Assert.True(result.HeadlessLikelihood > 0.9);
        Assert.True(result.Reasons.Count >= 3);
    }

    #endregion

    #region Browser Consistency Checks

    [Fact]
    public void Analyze_ChromeWithNoPlugins_Suspicious()
    {
        // window.chrome present, plugins=0 -- canonical headless Chrome pattern.
        var data = CreateRealChromeBrowserData();
        data.Headless!.PluginCount = 0;

        var result = _analyzer.Analyze(data, "test-request-12");

        Assert.True(result.HeadlessLikelihood >= 0.3);
        Assert.True(result.BrowserIntegrityScore < 100);
        Assert.Contains(result.Reasons, r => r.Contains("plugins"));
    }

    [Fact]
    public void Analyze_ZeroOuterDimensions_DetectsHeadless()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.OuterWidth = 0;
        data.Basics.OuterHeight = 0;

        var result = _analyzer.Analyze(data, "test-request-13");

        Assert.True(result.HeadlessLikelihood >= 0.3);
        Assert.True(result.BrowserIntegrityScore <= 70);
        Assert.Contains(result.Reasons, r => r.Contains("dimensions"));
    }

    [Fact]
    public void Analyze_InnerEqualsOuter_SlightlySuspicious()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.InnerWidth = 1920;
        data.Basics.InnerHeight = 1080;
        data.Basics.OuterWidth = 1920;
        data.Basics.OuterHeight = 1080;

        var result = _analyzer.Analyze(data, "test-request-14");

        Assert.True(result.HeadlessLikelihood >= 0.15);
        Assert.Contains(result.Reasons, r => r.Contains("browser chrome"));
    }

    #endregion

    #region Fingerprint Hash

    [Fact]
    public void Analyze_SameFingerprint_SameHash()
    {
        var data1 = CreateRealChromeBrowserData();
        var data2 = CreateRealChromeBrowserData();

        var result1 = _analyzer.Analyze(data1, "request-1");
        var result2 = _analyzer.Analyze(data2, "request-2");

        Assert.Equal(result1.FingerprintHash, result2.FingerprintHash);
    }

    [Fact]
    public void Analyze_DifferentScreenResolution_DifferentHash()
    {
        var data1 = CreateRealChromeBrowserData();
        var data2 = CreateRealChromeBrowserData();
        data2.Basics!.ScreenResolution = "2560x1440";

        var result1 = _analyzer.Analyze(data1, "request-1");
        var result2 = _analyzer.Analyze(data2, "request-2");

        Assert.NotEqual(result1.FingerprintHash, result2.FingerprintHash);
    }

    [Fact]
    public void Analyze_FingerprintHash_IsStable()
    {
        var data = CreateRealChromeBrowserData();

        var result1 = _analyzer.Analyze(data, "request-1");
        var result2 = _analyzer.Analyze(data, "request-2");
        var result3 = _analyzer.Analyze(data, "request-3");

        Assert.Equal(result1.FingerprintHash, result2.FingerprintHash);
        Assert.Equal(result2.FingerprintHash, result3.FingerprintHash);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Analyze_WithError_ReturnsErrorResult()
    {
        var data = new BrowserFingerprintData { Error = "Script blocked by CSP" };

        var result = _analyzer.Analyze(data, "test-request-error");

        Assert.Equal(0, result.BrowserIntegrityScore);
        Assert.Equal(0.5, result.HeadlessLikelihood); // Unknown
        Assert.Equal("error", result.FingerprintHash);
        Assert.Contains(result.Reasons, r => r.Contains("error"));
    }

    [Fact]
    public void Analyze_EmptyData_HandlesGracefully()
    {
        var data = new BrowserFingerprintData();

        var result = _analyzer.Analyze(data, "test-request-empty");

        Assert.NotNull(result);
        Assert.NotEmpty(result.FingerprintHash);
    }

    [Fact]
    public void Analyze_NullBlocks_HandlesGracefully()
    {
        // All blocks null. Analyzer must not throw -- this is the slim
        // adblocker-only beacon shape minus the adblocker flag.
        var data = new BrowserFingerprintData
        {
            Timestamp = 1700000000000,
            Basics = null,
            Tail = null,
            Headless = null,
            Touch = null
        };

        var result = _analyzer.Analyze(data, "test-request-nulls");

        Assert.NotNull(result);
    }

    #endregion

    #region Consistency Score

    [Fact]
    public void Analyze_MobilePlatformLargeScreen_ReducesConsistency()
    {
        // Claims iPhone but reports 4K resolution -- impossible.
        var data = CreateRealChromeBrowserData();
        data.Basics!.Platform = "iPhone";
        data.Basics.ScreenResolution = "3840x2160";

        var result = _analyzer.Analyze(data, "test-request-18");

        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    [Fact]
    public void Analyze_MissingTimezone_ReducesConsistency()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.Timezone = "";

        var result = _analyzer.Analyze(data, "test-request-19");

        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    [Fact]
    public void Analyze_MissingLanguage_ReducesConsistency()
    {
        var data = CreateRealChromeBrowserData();
        data.Basics!.Language = "";

        var result = _analyzer.Analyze(data, "test-request-20");

        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    #endregion

    #region Headless Browser Profiles

    [Fact]
    public void Analyze_TypicalPuppeteerProfile_Detected()
    {
        // Typical headless Chrome with Puppeteer: webdriver=1, CDP probe fires,
        // no plugins, no chrome.runtime, zero outer dims.
        var data = new BrowserFingerprintData
        {
            Timestamp = 1700000000000,
            CdpGetterHit = 1,
            Basics = new BasicsBlock
            {
                Timezone = "UTC",
                Language = "en-US",
                Languages = "en-US",
                Platform = "Linux x86_64",
                HardwareConcurrency = 4,
                DeviceMemory = 8,
                ScreenResolution = "800x600x24",
                DevicePixelRatio = 1,
                OuterWidth = 0,
                OuterHeight = 0,
                InnerWidth = 800,
                InnerHeight = 600,
                HasChromeObject = 1
            },
            Tail = new TailBlock { WebDriver = 1, Phantom = 0, Selenium = 0, Iframe = 0 },
            Headless = new HeadlessBlock { PluginCount = 0, HasChromeRuntime = 0, NotificationPermission = "default" },
            Touch = new TouchBlock { MaxTouchPoints = 0 }
        };

        var result = _analyzer.Analyze(data, "puppeteer-test");

        Assert.True(result.IsHeadless);
        Assert.True(result.HeadlessLikelihood >= 0.7);
        Assert.NotNull(result.DetectedAutomation);
        Assert.True(result.BrowserIntegrityScore < 70);
    }

    [Fact]
    public void Analyze_TypicalPlaywrightProfile_Detected()
    {
        var data = new BrowserFingerprintData
        {
            Timestamp = 1700000000000,
            CdpGetterHit = 1,
            Basics = new BasicsBlock
            {
                Timezone = "America/New_York",
                Language = "en-US",
                Languages = "en-US",
                Platform = "Win32",
                HardwareConcurrency = 4,
                DeviceMemory = 8,
                ScreenResolution = "1280x720x24",
                DevicePixelRatio = 1,
                OuterWidth = 1280,
                OuterHeight = 720,
                InnerWidth = 1280,
                InnerHeight = 720, // no browser chrome
                HasChromeObject = 1
            },
            Tail = new TailBlock { WebDriver = 1, Phantom = 0, Selenium = 0, Iframe = 0 },
            Headless = new HeadlessBlock { PluginCount = 0, HasChromeRuntime = 0, NotificationPermission = "default" },
            Touch = new TouchBlock { MaxTouchPoints = 0 }
        };

        var result = _analyzer.Analyze(data, "playwright-test");

        Assert.True(result.IsHeadless);
        Assert.NotNull(result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_SeleniumGridProfile_Detected()
    {
        var data = new BrowserFingerprintData
        {
            Timestamp = 1700000000000,
            Basics = new BasicsBlock
            {
                Timezone = "UTC",
                Language = "en-US",
                Languages = "en-US",
                Platform = "Linux x86_64",
                HardwareConcurrency = 2,
                DeviceMemory = 4,
                ScreenResolution = "1920x1080x24",
                DevicePixelRatio = 1,
                OuterWidth = 1920,
                OuterHeight = 1080,
                InnerWidth = 1920,
                InnerHeight = 1080,
                HasChromeObject = 1
            },
            Tail = new TailBlock { WebDriver = 1, Selenium = 1, Phantom = 0, Iframe = 0 },
            Headless = new HeadlessBlock { PluginCount = 2, HasChromeRuntime = 1, NotificationPermission = "default" },
            Touch = new TouchBlock { MaxTouchPoints = 0 }
        };

        var result = _analyzer.Analyze(data, "selenium-test");

        Assert.True(result.IsHeadless);
        Assert.Contains(new[] { "Selenium", "WebDriver" }, a => a == result.DetectedAutomation);
    }

    #endregion

    #region Stealth Mode Evasion Detection

    [Fact]
    public void Analyze_StealthModeAttempt_StillDetected()
    {
        // Bot trying to hide with realistic values but can't suppress CDP getter.
        var data = CreateRealChromeBrowserData();
        data.CdpGetterHit = 1;
        data.Tail!.WebDriver = 0;

        var result = _analyzer.Analyze(data, "stealth-test");

        Assert.True(result.HeadlessLikelihood >= 0.4);
        Assert.Equal("CDP/Puppeteer", result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_AllMarkersHidden_StillCaughtByIntegrity()
    {
        // Sophisticated bot hiding automation markers but other tells remain.
        var data = CreateRealChromeBrowserData();
        data.Headless!.PluginCount = 0; // Chrome with no plugins is rare
        data.Basics!.InnerWidth = data.Basics.OuterWidth; // No browser chrome
        data.Basics.InnerHeight = data.Basics.OuterHeight;

        var result = _analyzer.Analyze(data, "sophisticated-bot-test");

        Assert.True(result.BrowserIntegrityScore < 80);
        Assert.True(result.HeadlessLikelihood > 0.3);
    }

    #endregion
}
