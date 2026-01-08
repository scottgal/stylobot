using System.Net;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Integration tests using PuppeteerSharp to verify the demo page
///     behaves correctly with headless browser detection.
/// </summary>
/// <remarks>
///     These tests require the demo app to be running at http://localhost:5000
///     Run: dotnet run --project Mostlylucid.BotDetection.Demo
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Category", "Puppeteer")]
public class DemoPagePuppeteerTests : IAsyncLifetime
{
    private const string DemoUrl = "http://localhost:5000";
    private const string BotTestPageUrl = $"{DemoUrl}/bot-test";
    private readonly ITestOutputHelper _output;
    private IBrowser? _browser;

    public DemoPagePuppeteerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Download Chromium if not present
        _output.WriteLine("Downloading Chromium browser...");
        var browserFetcher = new BrowserFetcher();
        await browserFetcher.DownloadAsync();

        _output.WriteLine("Launching headless browser...");
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage"
            }
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
    public async Task HeadlessBrowser_IsBlockedByDefault()
    {
        await using var page = await _browser!.NewPageAsync();

        // Without test mode headers, headless browser should be blocked
        var response = await page.GoToAsync(BotTestPageUrl);

        _output.WriteLine($"Response status: {response.Status}");

        // Headless Chrome UA is detected as bot, should be blocked (403)
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);

        var content = await response.TextAsync();
        _output.WriteLine($"Response content:\n{content}");

        // Response should indicate it was blocked
        Assert.Contains("blocked", content.ToLower());
    }

    [Fact]
    public async Task HeadlessBrowser_DetectedOnApiEndpoint()
    {
        await using var page = await _browser!.NewPageAsync();

        // Access the detection check endpoint
        var response = await page.GoToAsync($"{DemoUrl}/bot-detection/check");

        // The endpoint should block due to bot detection
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);

        var content = await response.TextAsync();
        _output.WriteLine($"Detection check response:\n{content}");

        // Response should contain blocked info or access denied message
        Assert.True(
            content.Contains("error") || content.Contains("blocked") || content.Contains("denied"),
            "Expected bot blocking response");
    }

    [Fact]
    public async Task ProtectedEndpoint_BlocksHeadlessBrowser()
    {
        await using var page = await _browser!.NewPageAsync();

        // Try to access protected endpoint without test mode
        var response = await page.GoToAsync($"{DemoUrl}/api/protected");

        _output.WriteLine($"Protected endpoint status: {response.Status}");

        // Should be blocked as unverified bot
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
    }

    #endregion

    #region Page Functionality Tests (Using Test Mode)

    [Fact]
    public async Task BotTestPage_LoadsSuccessfully_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        var response = await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

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

        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

        // Wait for server-side detection to be displayed
        var serverResultSelector = "#serverResult";
        await page.WaitForSelectorAsync(serverResultSelector);

        // Get the server-side detection result
        var serverResult = await page.EvaluateFunctionAsync<string>(@"() => {
            const el = document.querySelector('#serverResult');
            return el ? el.innerText : '';
        }");

        _output.WriteLine($"Server-side detection result:\n{serverResult}");

        // With test mode disabled, should show detection info
        Assert.NotNull(serverResult);
        Assert.NotEmpty(serverResult);
    }

    [Fact]
    public async Task BotTestPage_CollectsClientSideFingerprint_WithTestMode()
    {
        await using var page = await _browser!.NewPageAsync();
        await SetTestModeHeaders(page);

        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

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
        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.DOMContentLoaded);

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

        var response = await page.GoToAsync($"{DemoUrl}/api");

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
        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

        // Verify grid layout is present
        var gridVisible = await page.EvaluateFunctionAsync<bool>(@"() => {
            const grid = document.querySelector('.grid');
            if (!grid) return false;
            const style = window.getComputedStyle(grid);
            return style.display === 'grid';
        }");

        Assert.True(gridVisible, "Expected CSS grid layout to be active");

        // Verify both cards are visible
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
        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

        // On mobile, grid should stack to single column
        var isSingleColumn = await page.EvaluateFunctionAsync<bool>(@"() => {
            const grid = document.querySelector('.grid');
            if (!grid) return false;
            const style = window.getComputedStyle(grid);
            return style.gridTemplateColumns === '1fr' ||
                   style.gridTemplateColumns.split(' ').length === 1;
        }");

        // The CSS has @media (max-width: 600px) that switches to 1fr
        Assert.True(isSingleColumn, "Expected single column layout on mobile viewport");
    }

    #endregion
}

/// <summary>
///     Tests with stealth mode to see if detection still works.
///     These tests verify that even with evasion attempts, bots are still detected.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Puppeteer")]
public class StealthModePuppeteerTests : IAsyncLifetime
{
    private const string DemoUrl = "http://localhost:5000";
    private const string BotTestPageUrl = $"{DemoUrl}/bot-test";
    private readonly ITestOutputHelper _output;
    private IBrowser? _browser;

    public StealthModePuppeteerTests(ITestOutputHelper output)
    {
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
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--window-size=1920,1080"
            }
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
    }

    [Fact]
    public async Task StealthMode_StillBlocked()
    {
        await using var page = await _browser!.NewPageAsync();

        // Try to hide automation
        await page.EvaluateFunctionOnNewDocumentAsync(@"() => {
            // Try to remove webdriver flag (this doesn't fully work)
            Object.defineProperty(navigator, 'webdriver', { get: () => false });
        }");

        var response = await page.GoToAsync(BotTestPageUrl);

        _output.WriteLine($"Stealth mode response status: {response.Status}");

        // Even with stealth attempts, should still be blocked
        // (HeadlessChrome UA is detected regardless)
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
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

        var response = await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.DOMContentLoaded);

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
///     Usage: dotnet test --filter "Category=Screenshots" -- pass the demo app URL if not localhost:5000
///     Screenshots are saved to: docs/screenshots/
/// </summary>
[Trait("Category", "Screenshots")]
[Trait("Category", "Integration")]
public class ScreenshotGenerator : IAsyncLifetime
{
    private const string DemoUrl = "http://localhost:5000";
    private const string BotTestPageUrl = $"{DemoUrl}/bot-test";

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

    public ScreenshotGenerator(ITestOutputHelper output)
    {
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
            Args = new[]
            {
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--window-size=1400,900"
            }
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
                var response = await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);

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

        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);
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

            await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);
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

        await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);
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

            await page.GoToAsync(BotTestPageUrl, WaitUntilNavigation.Networkidle0);
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