using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.ClientSide;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Comprehensive tests for the BrowserFingerprintAnalyzer.
///     These tests ensure bulletproof detection of headless browsers and automation.
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
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.NotificationPermission = "suspicious";

        // Act
        var result = _analyzer.Analyze(data, "test-request-17");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.25);
        Assert.Contains(result.Reasons, r => r.Contains("Permission"));
    }

    #endregion

    #region Helper Methods

    private static BrowserFingerprintData CreateRealChromeBrowserData()
    {
        return new BrowserFingerprintData
        {
            Platform = "Win32",
            Timezone = "America/New_York",
            Language = "en-US",
            Languages = "en-US,en",
            ScreenResolution = "1920x1080",
            AvailableResolution = "1920x1040",
            DevicePixelRatio = 1.25,
            HardwareConcurrency = 8,
            DeviceMemory = 8,
            HasTouch = 0,
            HasPdfPlugin = 1,
            PluginCount = 5,
            HasChromeObject = true,
            WebDriver = 0,
            Phantom = 0,
            Selenium = false,
            Nightmare = false,
            ChromeDevTools = 0,
            NotificationPermission = "default",
            OuterWidth = 1920,
            OuterHeight = 1040,
            InnerWidth = 1903,
            InnerHeight = 969,
            EvalLength = 33,
            BindIsNative = 1,
            WebGLVendor = "Google Inc.",
            WebGLRenderer = "ANGLE (Intel)",
            CanvasHash = "abc123def456"
        };
    }

    #endregion

    #region Real Browser Detection

    [Fact]
    public void Analyze_RealChromeBrowser_ReturnsHighIntegrity()
    {
        // Arrange - typical Chrome browser fingerprint
        var data = CreateRealChromeBrowserData();

        // Act
        var result = _analyzer.Analyze(data, "test-request-1");

        // Assert
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
        // Arrange - Firefox doesn't have chrome object
        var data = CreateRealChromeBrowserData();
        data.HasChromeObject = false;
        data.PluginCount = 3; // Firefox has fewer plugins typically

        // Act
        var result = _analyzer.Analyze(data, "test-request-2");

        // Assert
        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 80);
        Assert.Null(result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_RealSafariBrowser_ReturnsHighIntegrity()
    {
        // Arrange - Safari on macOS
        var data = CreateRealChromeBrowserData();
        data.Platform = "MacIntel";
        data.HasChromeObject = false;
        data.PluginCount = 2;
        data.DevicePixelRatio = 2; // Retina display

        // Act
        var result = _analyzer.Analyze(data, "test-request-3");

        // Assert
        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 80);
    }

    [Fact]
    public void Analyze_RealMobileBrowser_ReturnsHighIntegrity()
    {
        // Arrange - iPhone Safari
        var data = new BrowserFingerprintData
        {
            Platform = "iPhone",
            Timezone = "America/New_York",
            Language = "en-US",
            Languages = "en-US,en",
            ScreenResolution = "390x844",
            DevicePixelRatio = 3,
            HardwareConcurrency = 6,
            DeviceMemory = 0, // Not reported on iOS
            HasTouch = 1,
            PluginCount = 0, // iOS has no plugins
            HasChromeObject = false,
            WebDriver = 0,
            Phantom = 0,
            Selenium = false,
            Nightmare = false,
            ChromeDevTools = 0,
            OuterWidth = 390,
            OuterHeight = 844,
            InnerWidth = 390,
            InnerHeight = 664,
            EvalLength = 33,
            BindIsNative = 1
        };

        // Act
        var result = _analyzer.Analyze(data, "test-request-4");

        // Assert
        Assert.False(result.IsHeadless);
        Assert.True(result.BrowserIntegrityScore >= 70);
    }

    #endregion

    #region WebDriver Detection

    [Fact]
    public void Analyze_WebDriverTrue_DetectsAsHeadless()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.WebDriver = 1; // WebDriver flag set

        // Act
        var result = _analyzer.Analyze(data, "test-request-5");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.True(result.HeadlessLikelihood >= 0.5);
        Assert.Equal("WebDriver", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("webdriver"));
    }

    [Fact]
    public void Analyze_WebDriverWithRealBrowserData_StillDetectsBot()
    {
        // Arrange - sophisticated bot tries to look real but can't hide webdriver
        var data = CreateRealChromeBrowserData();
        data.WebDriver = 1;

        // Act
        var result = _analyzer.Analyze(data, "test-request-6");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.NotNull(result.DetectedAutomation);
    }

    #endregion

    #region Automation Framework Detection

    [Fact]
    public void Analyze_PhantomJS_DetectsAsHeadless()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.Phantom = 1;

        // Act
        var result = _analyzer.Analyze(data, "test-request-7");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.Equal("PhantomJS", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("PhantomJS"));
    }

    [Fact]
    public void Analyze_Nightmare_DetectsAsHeadless()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.Nightmare = true;

        // Act
        var result = _analyzer.Analyze(data, "test-request-8");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.Equal("Nightmare", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("Nightmare"));
    }

    [Fact]
    public void Analyze_Selenium_DetectsAsHeadless()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.Selenium = true;

        // Act
        var result = _analyzer.Analyze(data, "test-request-9");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.Equal("Selenium", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("Selenium"));
    }

    [Fact]
    public void Analyze_ChromeDevToolsProtocol_DetectsAsHeadless()
    {
        // Arrange - Puppeteer/Playwright using CDP
        var data = CreateRealChromeBrowserData();
        data.ChromeDevTools = 1;

        // Act
        var result = _analyzer.Analyze(data, "test-request-10");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.4);
        Assert.Equal("CDP/Puppeteer", result.DetectedAutomation);
        Assert.Contains(result.Reasons, r => r.Contains("DevTools"));
    }

    [Fact]
    public void Analyze_MultipleAutomationMarkers_DetectsFirst()
    {
        // Arrange - multiple automation frameworks detected
        var data = CreateRealChromeBrowserData();
        data.WebDriver = 1;
        data.Selenium = true;
        data.ChromeDevTools = 1;

        // Act
        var result = _analyzer.Analyze(data, "test-request-11");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.Equal("WebDriver", result.DetectedAutomation); // First detected
        Assert.True(result.HeadlessLikelihood > 0.9);
        Assert.True(result.Reasons.Count >= 3);
    }

    #endregion

    #region Browser Consistency Checks

    [Fact]
    public void Analyze_ChromeWithNoPlugins_Suspicious()
    {
        // Arrange - Chrome object present but no plugins (headless indicator)
        var data = CreateRealChromeBrowserData();
        data.PluginCount = 0;

        // Act
        var result = _analyzer.Analyze(data, "test-request-12");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.3);
        Assert.True(result.BrowserIntegrityScore < 100);
        Assert.Contains(result.Reasons, r => r.Contains("plugins"));
    }

    [Fact]
    public void Analyze_ZeroOuterDimensions_DetectsHeadless()
    {
        // Arrange - headless browser with no window
        var data = CreateRealChromeBrowserData();
        data.OuterWidth = 0;
        data.OuterHeight = 0;

        // Act
        var result = _analyzer.Analyze(data, "test-request-13");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.3);
        Assert.True(result.BrowserIntegrityScore <= 70);
        Assert.Contains(result.Reasons, r => r.Contains("dimensions"));
    }

    [Fact]
    public void Analyze_InnerEqualsOuter_SlightlySuspicious()
    {
        // Arrange - no browser chrome (inner == outer)
        var data = CreateRealChromeBrowserData();
        data.InnerWidth = 1920;
        data.InnerHeight = 1080;
        data.OuterWidth = 1920;
        data.OuterHeight = 1080;

        // Act
        var result = _analyzer.Analyze(data, "test-request-14");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.15);
        Assert.Contains(result.Reasons, r => r.Contains("browser chrome"));
    }

    #endregion

    #region Function Integrity

    [Fact]
    public void Analyze_NonNativeBind_Suspicious()
    {
        // Arrange - prototype pollution detected
        var data = CreateRealChromeBrowserData();
        data.BindIsNative = 0;

        // Act
        var result = _analyzer.Analyze(data, "test-request-15");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.2);
        Assert.Contains(result.Reasons, r => r.Contains("bind"));
    }

    [Fact]
    public void Analyze_SuspiciousEvalLength_Suspicious()
    {
        // Arrange - modified eval function
        var data = CreateRealChromeBrowserData();
        data.EvalLength = 100; // Normal is ~33-37

        // Act
        var result = _analyzer.Analyze(data, "test-request-16");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.15);
        Assert.Contains(result.Reasons, r => r.Contains("eval"));
    }

    [Theory]
    [InlineData(33)] // Normal
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    public void Analyze_NormalEvalLength_NoSuspicion(int evalLength)
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.EvalLength = evalLength;

        // Act
        var result = _analyzer.Analyze(data, $"test-request-eval-{evalLength}");

        // Assert - no reason about eval
        Assert.DoesNotContain(result.Reasons, r => r.Contains("eval"));
    }

    #endregion

    #region Fingerprint Hash

    [Fact]
    public void Analyze_SameFingerprint_SameHash()
    {
        // Arrange
        var data1 = CreateRealChromeBrowserData();
        var data2 = CreateRealChromeBrowserData();

        // Act
        var result1 = _analyzer.Analyze(data1, "request-1");
        var result2 = _analyzer.Analyze(data2, "request-2");

        // Assert
        Assert.Equal(result1.FingerprintHash, result2.FingerprintHash);
    }

    [Fact]
    public void Analyze_DifferentScreenResolution_DifferentHash()
    {
        // Arrange
        var data1 = CreateRealChromeBrowserData();
        var data2 = CreateRealChromeBrowserData();
        data2.ScreenResolution = "2560x1440";

        // Act
        var result1 = _analyzer.Analyze(data1, "request-1");
        var result2 = _analyzer.Analyze(data2, "request-2");

        // Assert
        Assert.NotEqual(result1.FingerprintHash, result2.FingerprintHash);
    }

    [Fact]
    public void Analyze_FingerprintHash_IsStable()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();

        // Act - analyze multiple times
        var result1 = _analyzer.Analyze(data, "request-1");
        var result2 = _analyzer.Analyze(data, "request-2");
        var result3 = _analyzer.Analyze(data, "request-3");

        // Assert - hash should be deterministic
        Assert.Equal(result1.FingerprintHash, result2.FingerprintHash);
        Assert.Equal(result2.FingerprintHash, result3.FingerprintHash);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void Analyze_WithError_ReturnsErrorResult()
    {
        // Arrange
        var data = new BrowserFingerprintData
        {
            Error = "Script blocked by CSP"
        };

        // Act
        var result = _analyzer.Analyze(data, "test-request-error");

        // Assert
        Assert.Equal(0, result.BrowserIntegrityScore);
        Assert.Equal(0.5, result.HeadlessLikelihood); // Unknown
        Assert.Equal("error", result.FingerprintHash);
        Assert.Contains(result.Reasons, r => r.Contains("error"));
    }

    [Fact]
    public void Analyze_EmptyData_HandlesGracefully()
    {
        // Arrange
        var data = new BrowserFingerprintData();

        // Act
        var result = _analyzer.Analyze(data, "test-request-empty");

        // Assert - should not throw
        Assert.NotNull(result);
        Assert.NotEmpty(result.FingerprintHash);
    }

    [Fact]
    public void Analyze_NullStrings_HandlesGracefully()
    {
        // Arrange
        var data = new BrowserFingerprintData
        {
            Timezone = null,
            Language = null,
            Platform = null,
            ScreenResolution = null
        };

        // Act
        var result = _analyzer.Analyze(data, "test-request-nulls");

        // Assert - should not throw
        Assert.NotNull(result);
    }

    #endregion

    #region Consistency Score

    [Fact]
    public void Analyze_MobilePlatformLargeScreen_ReducesConsistency()
    {
        // Arrange - claims to be iPhone but has huge screen
        var data = CreateRealChromeBrowserData();
        data.Platform = "iPhone";
        data.ScreenResolution = "3840x2160"; // 4K - impossible on iPhone

        // Act
        var result = _analyzer.Analyze(data, "test-request-18");

        // Assert
        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    [Fact]
    public void Analyze_MissingTimezone_ReducesConsistency()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.Timezone = "";

        // Act
        var result = _analyzer.Analyze(data, "test-request-19");

        // Assert
        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    [Fact]
    public void Analyze_MissingLanguage_ReducesConsistency()
    {
        // Arrange
        var data = CreateRealChromeBrowserData();
        data.Language = "";

        // Act
        var result = _analyzer.Analyze(data, "test-request-20");

        // Assert
        Assert.True(result.FingerprintConsistencyScore < 100);
    }

    #endregion

    #region Headless Browser Profiles

    [Fact]
    public void Analyze_TypicalPuppeteerProfile_Detected()
    {
        // Arrange - typical Puppeteer headless Chrome
        var data = new BrowserFingerprintData
        {
            Platform = "Linux x86_64",
            Timezone = "UTC",
            Language = "en-US",
            Languages = "en-US",
            ScreenResolution = "800x600",
            DevicePixelRatio = 1,
            HardwareConcurrency = 4,
            DeviceMemory = 8,
            HasTouch = 0,
            PluginCount = 0, // Headless Chrome has no plugins
            HasChromeObject = true,
            WebDriver = 1, // Puppeteer sets this
            Phantom = 0,
            Selenium = false,
            Nightmare = false,
            ChromeDevTools = 1, // CDP detected
            OuterWidth = 0, // No window
            OuterHeight = 0,
            InnerWidth = 800,
            InnerHeight = 600,
            EvalLength = 33,
            BindIsNative = 1
        };

        // Act
        var result = _analyzer.Analyze(data, "puppeteer-test");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.True(result.HeadlessLikelihood >= 0.7);
        Assert.NotNull(result.DetectedAutomation);
        Assert.True(result.BrowserIntegrityScore < 70); // Reduced threshold - multiple deductions but still calculated
    }

    [Fact]
    public void Analyze_TypicalPlaywrightProfile_Detected()
    {
        // Arrange - Playwright has similar markers to Puppeteer
        var data = new BrowserFingerprintData
        {
            Platform = "Win32",
            Timezone = "America/New_York",
            Language = "en-US",
            Languages = "en-US",
            ScreenResolution = "1280x720",
            DevicePixelRatio = 1,
            HardwareConcurrency = 4,
            DeviceMemory = 8,
            HasTouch = 0,
            PluginCount = 0,
            HasChromeObject = true,
            WebDriver = 1,
            Phantom = 0,
            Selenium = false,
            Nightmare = false,
            ChromeDevTools = 1,
            OuterWidth = 1280,
            OuterHeight = 720,
            InnerWidth = 1280,
            InnerHeight = 720, // Same as outer (no chrome)
            EvalLength = 33,
            BindIsNative = 1
        };

        // Act
        var result = _analyzer.Analyze(data, "playwright-test");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.NotNull(result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_SeleniumGridProfile_Detected()
    {
        // Arrange - Selenium Grid node
        var data = new BrowserFingerprintData
        {
            Platform = "Linux x86_64",
            Timezone = "UTC",
            Language = "en-US",
            Languages = "en-US",
            ScreenResolution = "1920x1080",
            DevicePixelRatio = 1,
            HardwareConcurrency = 2,
            DeviceMemory = 4,
            HasTouch = 0,
            PluginCount = 2,
            HasChromeObject = true,
            WebDriver = 1,
            Phantom = 0,
            Selenium = true, // Selenium markers present
            Nightmare = false,
            ChromeDevTools = 0,
            OuterWidth = 1920,
            OuterHeight = 1080,
            InnerWidth = 1920,
            InnerHeight = 1080,
            EvalLength = 33,
            BindIsNative = 1
        };

        // Act
        var result = _analyzer.Analyze(data, "selenium-test");

        // Assert
        Assert.True(result.IsHeadless);
        Assert.Contains(new[] { "Selenium", "WebDriver" }, a => a == result.DetectedAutomation);
    }

    #endregion

    #region Stealth Mode Evasion Detection

    [Fact]
    public void Analyze_StealthModeAttempt_StillDetected()
    {
        // Arrange - bot trying to hide with realistic values but forgot CDP marker
        var data = CreateRealChromeBrowserData();
        data.ChromeDevTools = 1; // Can't hide this easily
        data.WebDriver = 0; // Tried to hide

        // Act
        var result = _analyzer.Analyze(data, "stealth-test");

        // Assert
        Assert.True(result.HeadlessLikelihood >= 0.4);
        Assert.Equal("CDP/Puppeteer", result.DetectedAutomation);
    }

    [Fact]
    public void Analyze_AllMarkersHidden_StillCaughtByIntegrity()
    {
        // Arrange - sophisticated bot hiding automation markers but has suspicious signals
        var data = CreateRealChromeBrowserData();
        data.PluginCount = 0; // Chrome with no plugins is rare
        data.InnerWidth = data.OuterWidth; // No browser chrome
        data.InnerHeight = data.OuterHeight;

        // Act
        var result = _analyzer.Analyze(data, "sophisticated-bot-test");

        // Assert
        Assert.True(result.BrowserIntegrityScore < 80);
        Assert.True(result.HeadlessLikelihood > 0.3);
    }

    #endregion
}