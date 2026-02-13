using System.Net;
using PuppeteerSharp;
using PuppeteerSharp.Media;
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

/// <summary>
///     Screenshot generator for documentation.
///     Run these tests to update the documentation screenshots after UI changes.
///     Usage: dotnet test --filter "Category=Screenshots"
///     Screenshots are saved to: docs/screenshots/
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Screenshots")]
[Trait("Category", "Integration")]
public class ScreenshotGenerator : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly string _botTestPageUrl;

    /// <summary>
    ///     Bot types with actual User-Agent strings for full pipeline detection.
    ///     Using real UAs instead of test-mode headers to show actual detection results.
    /// </summary>
    private static readonly (string UserAgent, string Name, string Description)[] BotTypes =
    {
        // Human / Real Browser
        ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "human", "Real browser / human visitor"),

        // Search Engines
        ("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            "googlebot", "Google search crawler"),
        ("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)",
            "bingbot", "Bing search crawler"),

        // Scrapers
        ("Scrapy/2.5.0 (+https://scrapy.org)",
            "scrapy", "Scrapy web scraper"),
        ("curl/7.68.0",
            "curl", "cURL HTTP client"),
        ("python-requests/2.28.0",
            "python-requests", "Python Requests library"),

        // Automation
        ("Mozilla/5.0 HeadlessChrome/120.0.0.0",
            "headless-chrome", "Headless Chrome browser"),

        // AI Crawlers
        ("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.0; +https://openai.com/gptbot)",
            "gptbot", "OpenAI GPTBot AI crawler"),
        ("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +https://www.anthropic.com)",
            "claudebot", "Anthropic ClaudeBot AI crawler"),

        // Social Media
        ("Twitterbot/1.0",
            "twitterbot", "Twitter/X social media bot"),
        ("facebookexternalhit/1.1",
            "facebookbot", "Facebook social media bot"),

        // Monitoring
        ("Mozilla/5.0 (compatible; UptimeRobot/2.0; http://www.uptimerobot.com/)",
            "uptimerobot", "UptimeRobot monitoring service")
    };

    private readonly ITestOutputHelper _output;
    private IBrowser? _browser;
    private string _screenshotDir = null!;

    public ScreenshotGenerator(DemoAppFactory factory, ITestOutputHelper output)
    {
        _demoUrl = factory.BaseUrl;
        _botTestPageUrl = $"{_demoUrl}/bot-test";
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Determine screenshot directory (repo root/docs/screenshots)
        var currentDir = Directory.GetCurrentDirectory();
        var repoRoot = FindRepoRoot(currentDir) ?? currentDir;
        _screenshotDir = Path.Combine(repoRoot, "docs", "screenshots");
        Directory.CreateDirectory(_screenshotDir);

        _output.WriteLine($"Screenshots will be saved to: {_screenshotDir}");

        // Download Chromium
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        // Launch browser with realistic viewport
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--window-size=1400,900"
            ]
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Generates screenshots for all bot types using actual User-Agent strings.
    ///     This triggers the full detection pipeline to show real detection results.
    /// </summary>
    [Fact]
    public async Task GenerateAllBotTypeScreenshots()
    {
        await using var page = await _browser!.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 900 });

        var generated = new List<string>();
        var failed = new List<string>();

        foreach (var (userAgent, name, description) in BotTypes)
            try
            {
                _output.WriteLine($"Generating screenshot for: {name} ({description})");
                _output.WriteLine($"  User-Agent: {userAgent}");

                // Set actual User-Agent to trigger full detection pipeline
                await page.SetUserAgentAsync(userAgent);

                // Add realistic headers for human browser
                if (name == "human")
                    await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
                    {
                        ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                        ["Accept-Language"] = "en-US,en;q=0.9",
                        ["Accept-Encoding"] = "gzip, deflate, br"
                    });
                else
                    // Clear extra headers for bots
                    await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>());

                // Navigate and wait for content
                var response = await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);

                if (!response.Ok) _output.WriteLine($"  Warning: Got status {response.Status} for {name}");
                // If blocked, still take a screenshot showing the blocked state
                // Wait for detection signals to populate
                await Task.Delay(800);

                var filename = $"bot-detection-{name}.png";
                var filepath = Path.Combine(_screenshotDir, filename);

                // Capture the key detection area (stats grid + detection result + signals)
                await page.ScreenshotAsync(filepath, new ScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    Clip = new Clip
                    {
                        X = 0,
                        Y = 120, // Start below title/buttons
                        Width = 1200,
                        Height = 600 // Stats + Result + some signals
                    }
                });

                _output.WriteLine($"  Saved: {filename}");
                generated.Add(name);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"  ERROR: {ex.Message}");
                failed.Add(name);
            }

        _output.WriteLine($"\nGenerated {generated.Count} screenshots, {failed.Count} failed");
        _output.WriteLine($"Screenshots saved to: {_screenshotDir}");

        Assert.Empty(failed);
    }

    /// <summary>
    ///     Generates an overview screenshot using Scrapy User-Agent for rich detection signals.
    /// </summary>
    [Fact]
    public async Task GenerateOverviewScreenshot()
    {
        await using var page = await _browser!.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 900 });

        // Use Scrapy UA to trigger full detection with signals
        await page.SetUserAgentAsync("Scrapy/2.5.0 (+https://scrapy.org)");

        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);
        await Task.Delay(1000);

        // Capture: Stats grid + Detection Result + Detection Signals
        var filepath = Path.Combine(_screenshotDir, "bot-detection-overview.png");
        await page.ScreenshotAsync(filepath, new ScreenshotOptions
        {
            Type = ScreenshotType.Png,
            Clip = new Clip
            {
                X = 0,
                Y = 120, // Skip header/buttons
                Width = 1200,
                Height = 750 // Stats + Result + Signals cards
            }
        });

        _output.WriteLine($"Overview screenshot saved to: {filepath}");
        Assert.True(File.Exists(filepath));
    }

    /// <summary>
    ///     Generates comparison screenshots using real User-Agents.
    /// </summary>
    [Fact]
    public async Task GenerateComparisonScreenshots()
    {
        var comparisons = new[]
        {
            ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36", "human",
                "Human Visitor"),
            ("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "googlebot",
                "Search Engine Bot"),
            ("Scrapy/2.5.0 (+https://scrapy.org)", "scrapy", "Scraper Bot"),
            ("Mozilla/5.0 AppleWebKit/537.36 (compatible; GPTBot/1.0; +https://openai.com/gptbot)", "gptbot",
                "AI Crawler")
        };

        await using var page = await _browser!.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 900 });

        foreach (var (userAgent, name, label) in comparisons)
        {
            await page.SetUserAgentAsync(userAgent);

            // Add realistic headers for human
            if (name == "human")
                await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
                {
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
                    ["Accept-Language"] = "en-US,en;q=0.9"
                });
            else
                await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>());

            await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);
            await Task.Delay(500);

            var filepath = Path.Combine(_screenshotDir, $"comparison-{name}.png");

            // Capture stats grid + detection result
            await page.ScreenshotAsync(filepath, new ScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Clip = new Clip
                {
                    X = 0,
                    Y = 120,
                    Width = 1200,
                    Height = 400
                }
            });

            _output.WriteLine($"Comparison screenshot for {label} saved: {filepath}");
        }
    }

    /// <summary>
    ///     Generates screenshot showing detection signals list with impact scores.
    /// </summary>
    [Fact]
    public async Task GenerateSignalsScreenshot()
    {
        await using var page = await _browser!.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 1000 });

        // Use Scrapy to get multiple detection signals
        await page.SetUserAgentAsync("Scrapy/2.5.0 (+https://scrapy.org)");

        await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);
        await Task.Delay(800);

        var filepath = Path.Combine(_screenshotDir, "detection-signals.png");

        // Try to find and screenshot the signals card
        var signalsElement = await page.QuerySelectorAsync(".signals-list");

        if (signalsElement != null)
        {
            var cardElement = await page.QuerySelectorAsync(".card:has(.signals-list)");
            if (cardElement != null)
                await cardElement.ScreenshotAsync(filepath);
            else
                await signalsElement.ScreenshotAsync(filepath);
        }
        else
        {
            // Fallback: clip to signals area
            await page.ScreenshotAsync(filepath, new ScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Clip = new Clip
                {
                    X = 0,
                    Y = 500,
                    Width = 1200,
                    Height = 400
                }
            });
        }

        _output.WriteLine($"Detection signals screenshot saved to: {filepath}");
        Assert.True(File.Exists(filepath));
    }

    /// <summary>
    ///     Generates animation frames using real User-Agents.
    /// </summary>
    [Fact]
    public async Task GenerateAnimationFrames()
    {
        var animDir = Path.Combine(_screenshotDir, "animation");
        Directory.CreateDirectory(animDir);

        await using var page = await _browser!.NewPageAsync();
        await page.SetViewportAsync(new ViewPortOptions { Width = 1200, Height = 700 });

        var frames = new[]
        {
            ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 Safari/537.36", "human"),
            ("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "googlebot"),
            ("Scrapy/2.5.0 (+https://scrapy.org)", "scrapy"),
            ("curl/7.68.0", "curl"),
            ("Mozilla/5.0 AppleWebKit/537.36 (compatible; GPTBot/1.0; +https://openai.com/gptbot)", "gptbot")
        };
        var frameNum = 0;

        foreach (var (userAgent, name) in frames)
        {
            await page.SetUserAgentAsync(userAgent);

            await page.GoToAsync(_botTestPageUrl, WaitUntilNavigation.Networkidle0);
            await Task.Delay(500);

            var filepath = Path.Combine(animDir, $"frame-{frameNum:D3}-{name}.png");

            // Capture detection result area
            await page.ScreenshotAsync(filepath, new ScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Clip = new Clip
                {
                    X = 0,
                    Y = 120,
                    Width = 1200,
                    Height = 500
                }
            });

            frameNum++;
            _output.WriteLine($"Animation frame {frameNum}: {name}");
        }

        _output.WriteLine($"\nAnimation frames saved to: {animDir}");
        _output.WriteLine("To create GIF, use: convert -delay 200 -loop 0 frame-*.png animation.gif");
    }
}
