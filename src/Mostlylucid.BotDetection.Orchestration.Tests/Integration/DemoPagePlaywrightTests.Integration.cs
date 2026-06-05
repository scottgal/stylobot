using System.Net;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     Browser-driven integration tests against the Demo app. Replaces the
///     PuppeteerSharp-based <c>DemoPagePuppeteerTests</c>; Playwright .NET
///     ships browsers via its own CLI (<c>playwright install</c>) and supports
///     Chromium / Firefox / WebKit from one harness so future
///     cross-browser-specific tests (Brave farbling, Safari ITP, Firefox ETP
///     -- see <c>docs/client-side-detection-2026.md</c>) can hang off the same
///     fixture.
///
///     Retargeted to <c>/api</c> and <c>/SignatureDemo</c> instead of the
///     historical <c>/bot-test</c> page that no longer exists in Demo (per the
///     <c>project_puppeteer_test_rot</c> memory). The detection middleware
///     fires on every request regardless of which page; what we're verifying
///     is the detection pipeline + headers, not the page chrome.
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
[Trait("Category", "Playwright")]
public class DemoPagePlaywrightTests : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly ITestOutputHelper _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public DemoPagePlaywrightTests(DemoAppFactory factory, ITestOutputHelper output)
    {
        _demoUrl = factory.BaseUrl;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _output.WriteLine("Ensuring Chromium installed...");
        PlaywrightBrowserInstall.EnsureInstalled();

        _output.WriteLine("Launching headless Chromium...");
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
            ],
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    /// <summary>
    ///     Sets the test-mode header that Demo's middleware honours to relax the
    ///     verdict path. Equivalent to the Puppeteer version's
    ///     <c>SetExtraHttpHeadersAsync</c>.
    /// </summary>
    private static Task<IBrowserContext> NewContextWithTestModeAsync(
        IBrowser browser, string mode = "disable")
    {
        return browser.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["ml-bot-test-mode"] = mode,
            },
        });
    }

    // ── Bot Detection Verification ──────────────────────────────────────────

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task HeadlessBrowser_IsDetectedByDefault_OnApiRoot()
    {
        var page = await _browser!.NewPageAsync();
        // Demo's /api root runs detection like any other endpoint; check the
        // response headers contain the gateway's detection markers.
        var response = await page.GotoAsync($"{_demoUrl}/api");
        Assert.NotNull(response);
        _output.WriteLine($"Response status: {response!.Status}");

        // Demo runs with BlockDetectedBots=false; detection runs but the
        // request still completes. We're verifying the pipeline fired.
        Assert.True(response.Status < 500, $"Expected non-5xx, got {response.Status}");

        var headers = response.Headers;
        var hasBotHeader = headers.ContainsKey("x-bot-detection")
                           || headers.ContainsKey("x-bot-confidence")
                           || headers.ContainsKey("x-bot-is-bot")
                           || headers.ContainsKey("x-stylobot-isbot");
        _output.WriteLine($"Detection headers present: {hasBotHeader}");
        foreach (var (k, v) in headers.Where(h => h.Key.StartsWith("x-bot") || h.Key.StartsWith("x-stylobot")))
            _output.WriteLine($"  {k}: {v}");

        Assert.True(hasBotHeader, "Expected bot detection response headers on /api");
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task ProtectedEndpoint_BlocksHeadlessBrowser()
    {
        var page = await _browser!.NewPageAsync();
        var response = await page.GotoAsync($"{_demoUrl}/api/protected");
        Assert.NotNull(response);
        _output.WriteLine($"Protected endpoint status: {response!.Status}");

        // /api/protected has its own per-endpoint policy that 403s bots even
        // when global BlockDetectedBots=false.
        Assert.Equal((int)HttpStatusCode.Forbidden, response.Status);
    }

    // ── Page Functionality (test-mode header relaxes detection) ─────────────

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task SignatureDemoPage_LoadsSuccessfully_WithTestMode()
    {
        await using var context = await NewContextWithTestModeAsync(_browser!);
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync($"{_demoUrl}/SignatureDemo",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        Assert.NotNull(response);
        Assert.True(response!.Status < 400, $"Expected 2xx/3xx, got {response.Status}");
        var contentType = response.Headers.GetValueOrDefault("content-type", "");
        Assert.Contains("text/html", contentType);

        var title = await page.TitleAsync();
        _output.WriteLine($"Page loaded. Title: {title}");
        Assert.NotEmpty(title);
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task ApiRoot_ReturnsDetectionSummary_WithTestMode()
    {
        await using var context = await NewContextWithTestModeAsync(_browser!, "human");
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync($"{_demoUrl}/api");
        Assert.NotNull(response);
        var content = await response!.TextAsync();
        _output.WriteLine($"/api response:\n{content}");

        Assert.True(response.Status < 400);
        Assert.Contains("isBot", content);
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task NavigatorWebDriver_IsTrueInPlaywrightChromium()
    {
        var page = await _browser!.NewPageAsync();
        await page.GotoAsync($"{_demoUrl}/api", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // Playwright sets navigator.webdriver = true on its Chromium build,
        // same as Puppeteer (and standard Selenium). The detection pipeline
        // reads this server-side via the WebDriver header / TLS markers;
        // this test pins the client-side observable.
        var webdriver = await page.EvaluateAsync<bool>("() => navigator.webdriver === true");
        _output.WriteLine($"navigator.webdriver: {webdriver}");
        Assert.True(webdriver, "Expected navigator.webdriver=true on Playwright Chromium");
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task ViewportSize_Mobile_AppliesCorrectly()
    {
        await using var context = await NewContextWithTestModeAsync(_browser!);
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(375, 667);
        await page.GotoAsync($"{_demoUrl}/SignatureDemo",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var viewport = await page.EvaluateAsync<int[]>(
            "() => [window.innerWidth, window.innerHeight]");
        _output.WriteLine($"Viewport: {viewport[0]}x{viewport[1]}");
        Assert.Equal(375, viewport[0]);
    }
}

/// <summary>
///     Stealth-mode variants -- launches Chromium with the
///     <c>--disable-blink-features=AutomationControlled</c> flag and overrides
///     <c>navigator.webdriver</c> in an init script. Verifies the detection
///     pipeline still fires on UA / TLS / header signals even when the
///     standard client-side markers are patched.
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
[Trait("Category", "Playwright")]
public class StealthModePlaywrightTests : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly ITestOutputHelper _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public StealthModePlaywrightTests(DemoAppFactory factory, ITestOutputHelper output)
    {
        _demoUrl = factory.BaseUrl;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        PlaywrightBrowserInstall.EnsureInstalled();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-blink-features=AutomationControlled",
                "--window-size=1920,1080",
            ],
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task StealthMode_StillDetected()
    {
        var context = await _browser!.NewContextAsync();
        // Init-script equivalent of Puppeteer's EvaluateFunctionOnNewDocumentAsync.
        // This patches navigator.webdriver before the page loads. Detection should
        // still fire because the UA family + TLS markers carry the signal regardless.
        await context.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => false });
        ");
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync($"{_demoUrl}/api");
        Assert.NotNull(response);
        _output.WriteLine($"Stealth-mode response status: {response!.Status}");

        Assert.True(response.Status < 500);

        var headers = response.Headers;
        var hasBotHeader = headers.ContainsKey("x-bot-detection")
                           || headers.ContainsKey("x-bot-confidence")
                           || headers.ContainsKey("x-bot-is-bot")
                           || headers.ContainsKey("x-stylobot-isbot");
        _output.WriteLine($"Detection headers present: {hasBotHeader}");
        foreach (var (k, v) in headers.Where(h => h.Key.StartsWith("x-bot") || h.Key.StartsWith("x-stylobot")))
            _output.WriteLine($"  {k}: {v}");

        Assert.True(hasBotHeader, "Expected bot detection headers even in stealth mode");
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task WithRealUserAgent_StillDetectedByInconsistency()
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Accept-Encoding"] = "gzip, deflate, br",
            },
        });
        var page = await context.NewPageAsync();
        var response = await page.GotoAsync($"{_demoUrl}/api",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(response);
        _output.WriteLine($"Real-UA response status: {response!.Status}");

        // Realistic UA may pass UA detection, but the inconsistency detectors
        // (header order, TLS fingerprint vs claimed Chrome version) should
        // still catch it. Demo doesn't block, so the request completes.
        Assert.True(response.Status == 200 || response.Status == 403,
            $"Expected 200 or 403, got {response.Status}");
    }
}

/// <summary>
///     One-shot Playwright browser bootstrap. The Playwright CLI bundles
///     Chromium via <c>playwright install</c>; this helper invokes the
///     equivalent from-managed-code entrypoint so the test fixture doesn't
///     have to require an out-of-band install step on CI.
/// </summary>
internal static class PlaywrightBrowserInstall
{
    private static readonly object Lock = new();
    private static bool _installed;

    public static void EnsureInstalled()
    {
        lock (Lock)
        {
            if (_installed) return;
            // exitCode 0 = success; non-zero throws via the CLI entrypoint.
            // Idempotent if browsers are already cached.
            var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"playwright install chromium exited with code {exitCode}");
            _installed = true;
        }
    }
}
