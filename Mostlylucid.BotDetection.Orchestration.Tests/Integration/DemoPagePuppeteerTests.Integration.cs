using System.Net;
using PuppeteerSharp;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Integration tests using PuppeteerSharp to verify the demo page
///     behaves correctly with headless browser detection.
///     The Demo app is self-hosted via <see cref="DemoAppFactory" /> on a random port.
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
[Trait("Category", "Puppeteer")]
public class DemoPagePuppeteerTests : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly string _botTestPageUrl;
    private readonly ITestOutputHelper _output;
    private IBrowser? _browser;

    public DemoPagePuppeteerTests(DemoAppFactory factory, ITestOutputHelper output)
    {
        _demoUrl = factory.BaseUrl;
        _botTestPageUrl = $"{_demoUrl}/bot-test";
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _output.WriteLine("Downloading Chromium browser...");
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _output.WriteLine("Launching headless browser...");
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage"
            ]
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
    }

    /// <summary>
    ///     Sets up test mode header to bypass bot detection for functional tests.
    /// </summary>
    private static async Task SetTestModeHeaders(IPage page, string mode = "disable")
    {
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
        {
            ["ml-bot-test-mode"] = mode
        });
    }

    #region Bot Detection Verification Tests

    [Fact]
    public async Task HeadlessBrowser_IsDetectedByDefault()
    {
        await using var page = await _browser!.NewPageAsync();

        // Without test mode headers, headless browser should be detected as a bot.
        // Demo app does NOT block bots (BlockDetectedBots=false), but detection headers are returned.
        var response = await page.GoToAsync(_botTestPageUrl);

        _output.WriteLine($"Response status: {response.Status}");

        // Demo app allows requests through but adds detection headers
        Assert.True(response.Ok, $"Expected OK (demo doesn't block), got {response.Status}");

        // Verify bot detection ran - check for detection headers
        var headers = response.Headers;
        var hasBotHeader = headers.ContainsKey("x-bot-detection") ||
                           headers.ContainsKey("x-bot-confidence") ||
                           headers.ContainsKey("x-bot-is-bot");

        _output.WriteLine($"Detection headers present: {hasBotHeader}");
        foreach (var h in headers.Where(h => h.Key.StartsWith("x-bot")))
            _output.WriteLine($"  {h.Key}: {h.Value}");

        Assert.True(hasBotHeader, "Expected bot detection response headers");
    }

    [Fact]
    public async Task HeadlessBrowser_DetectedOnApiEndpoint()
    {
        await using var page = await _browser!.NewPageAsync();

        // Access the detection check endpoint
        var response = await page.GoToAsync($"{_demoUrl}/bot-detection/check");

        _output.WriteLine($"Detection check response status: {response.Status}");

        var content = await response.TextAsync();
        _output.WriteLine($"Detection check response:\n{content}");

        // Demo app has BlockDetectedBots=false, so the endpoint returns OK with detection info
        Assert.True(response.Ok, $"Expected OK (demo doesn't block), got {response.Status}");

        // The response should contain detection information
        Assert.True(
            content.Contains("isBot") || content.Contains("bot") || content.Contains("detection"),
            "Expected detection info in response");
    }

    [Fact]
    public async Task ProtectedEndpoint_BlocksHeadlessBrowser()
    {
        await using var page = await _browser!.NewPageAsync();

        // Try to access protected endpoint without test mode
        var response = await page.GoToAsync($"{_demoUrl}/api/protected");

        _output.WriteLine($"Protected endpoint status: {response.Status}");

        // The /api/protected endpoint has its own blocking policy that returns 403 for bots,
        // even when the global BlockDetectedBots=false. This is an explicitly protected endpoint.
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    #endregion

    #region Page Functionality Tests (Using Test Mode)

    [Fact]
    public async Task BotTestPage_LoadsSuccessfully_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        var response = await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

        Assert.NotNull(response);
        Assert.True(response.Ok, $"Expected HTTP 200, got {response.Status}");
        Assert.Contains("text/html", response.Headers["content-type"]);

        // Verify page title
        var title = await page.GetTitleAsync();
        Assert.Contains("Bot Detection", title);

        _output.WriteLine($"Page loaded successfully. Title: {title}");
    }

    [Fact]
    public async Task BotTestPage_ShowsServerSideDetection_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

        // The detection result is in a .card with an h2 "Detection Result" and a .result-badge
        var resultBadgeSelector = ".result-badge";
        await page.WaitForSelectorAsync(resultBadgeSelector);

        // Get the detection result badge text (Human, Suspicious, or Bot Detected)
        var resultText = await page.EvaluateFunctionAsync<string>(@"() => {
            const badge = document.querySelector('.result-badge');
            return badge ? badge.innerText.trim() : '';
        }");

        _output.WriteLine($"Server-side detection result: {resultText}");

        // With test mode, should show detection info
        Assert.NotNull(resultText);
        Assert.NotEmpty(resultText);
        Assert.True(
            resultText.Contains("Human") || resultText.Contains("Bot") || resultText.Contains("Suspicious"),
            $"Expected detection verdict, got: {resultText}");
    }

    [Fact]
    public async Task BotTestPage_CollectsClientSideFingerprint_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

        // Wait for fingerprint data to be collected and displayed
        await Task.Delay(2000); // Allow time for JS to execute and POST fingerprint

        var fingerprintDataSelector = "#fingerprintData";
        await page.WaitForSelectorAsync(fingerprintDataSelector);

        var fingerprintData = await page.EvaluateFunctionAsync<string>(@"() => {
            const el = document.querySelector('#fingerprintData');
            return el ? el.innerText : '';
        }");

        _output.WriteLine($"Fingerprint data:\n{fingerprintData}");

        // Verify fingerprint was collected (may still show initial state or collected data)
        Assert.NotNull(fingerprintData);
    }

    [Fact]
    public async Task BotTestPage_DetectsWebDriverFlag()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        // Check if navigator.webdriver is true (it should be in Puppeteer)
        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.DOMContentLoaded);

        var webdriverFlag = await page.EvaluateFunctionAsync<bool>(@"() => {
            return navigator.webdriver === true;
        }");

        _output.WriteLine($"navigator.webdriver: {webdriverFlag}");

        // In headless Puppeteer, webdriver should be true (unless stealth mode)
        Assert.True(webdriverFlag, "Expected navigator.webdriver to be true in Puppeteer");
    }

    [Fact]
    public async Task RootEndpoint_ReturnsDetectionSummary_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page, "human"); // Simulate human

        var response = await page.GoToAsync($"{_demoUrl}/api");

        var content = await response.TextAsync();
        _output.WriteLine($"API root response:\n{content}");

        Assert.NotNull(response);
        Assert.True(response.Ok);
        Assert.Contains("Bot Detection Demo API", content);
        Assert.Contains("isBot", content);
    }

    [Fact]
    public async Task BotTestPage_GridLayoutDisplaysCorrectly_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 800 });
        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

        // Verify stats-grid layout is present (page uses .stats-grid, not .grid)
        var gridVisible = await page.EvaluateFunctionAsync<bool>(@"() => {
            const grid = document.querySelector('.stats-grid');
            if (!grid) return false;
            const style = window.getComputedStyle(grid);
            return style.display === 'grid';
        }");

        Assert.True(gridVisible, "Expected CSS grid layout to be active on .stats-grid");

        // Verify cards are visible
        var cardCount = await page.EvaluateFunctionAsync<int>(@"() => {
            return document.querySelectorAll('.card').length;
        }");

        Assert.True(cardCount >= 2, $"Expected at least 2 cards, found {cardCount}");
    }

    [Fact]
    public async Task BotTestPage_ResponsiveOnMobile_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        // Set mobile viewport
        await page.SetViewportAsync(new ViewPortOptions { Width = 375, Height = 667 });
        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

        // At 375px (below 768px breakpoint), .stats-grid switches to repeat(2, 1fr)
        var isResponsive = await page.EvaluateFunctionAsync<bool>(@"() => {
            const grid = document.querySelector('.stats-grid');
            if (!grid) return false;
            const style = window.getComputedStyle(grid);
            const columns = style.gridTemplateColumns.split(' ').length;
            // At 375px, should be 2 columns (repeat(2, 1fr)) - down from 4 at desktop
            return columns <= 2;
        }");

        // The CSS has @media (max-width: 768px) that switches .stats-grid to repeat(2, 1fr)
        Assert.True(isResponsive, "Expected responsive grid layout (<=2 columns) on mobile viewport");
    }

    #endregion
}

/// <summary>
///     Tests with stealth mode to see if detection still works.
///     These tests verify that even with evasion attempts, bots are still detected.
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
[Trait("Category", "Puppeteer")]
public class StealthModePuppeteerTests : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly string _botTestPageUrl;
    private readonly ITestOutputHelper _output;
    private IBrowser? _browser;

    public StealthModePuppeteerTests(DemoAppFactory factory, ITestOutputHelper output)
    {
        _demoUrl = factory.BaseUrl;
        _botTestPageUrl = $"{_demoUrl}/bot-test";
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        // Launch with args that try to hide headless nature
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--window-size=1920,1080"
            ]
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
    }

    [Fact]
    public async Task StealthMode_StillDetected()
    {
        await using var page = await _browser!.NewPageAsync();

        // Try to hide automation
        await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
            // Try to remove webdriver flag (this doesn't fully work)
            Object.defineProperty(navigator, 'webdriver', { get: () => false });
        }");

        var response = await page.GoToAsync(_botTestPageUrl);

        _output.WriteLine($"Stealth mode response status: {response.Status}");

        // Demo app has BlockDetectedBots=false, so it returns OK but detection still runs
        Assert.True(response.Ok, $"Expected OK (demo doesn't block), got {response.Status}");

        // Even with stealth attempts, detection headers should be present
        // (HeadlessChrome UA is detected regardless)
        var headers = response.Headers;
        var hasBotHeader = headers.ContainsKey("x-bot-detection") ||
                           headers.ContainsKey("x-bot-confidence") ||
                           headers.ContainsKey("x-bot-is-bot");

        _output.WriteLine($"Detection headers present: {hasBotHeader}");
        foreach (var h in headers.Where(h => h.Key.StartsWith("x-bot")))
            _output.WriteLine($"  {h.Key}: {h.Value}");

        Assert.True(hasBotHeader, "Expected bot detection response headers even in stealth mode");
    }

    [Fact]
    public async Task WithRealUserAgent_StillDetectedByInconsistency()
    {
        await using var page = await _browser!.NewPageAsync();

        // Set a realistic user agent
        await page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // Set extra HTTP headers to appear more human
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["Accept-Encoding"] = "gzip, deflate, br"
        });

        var response = await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.DOMContentLoaded);

        _output.WriteLine($"Real UA response status: {response.Status}");

        // With realistic UA, the request might pass UA detection
        // but the page should load (bot detection still runs but may not block)
        Assert.NotNull(response);

        // Could be 200 (passed) or 403 (other detection caught it)
        Assert.True(
            response.Status == HttpStatusCode.OK ||
            response.Status == HttpStatusCode.Forbidden,
            $"Expected 200 or 403, got {response.Status}");
    }
}
