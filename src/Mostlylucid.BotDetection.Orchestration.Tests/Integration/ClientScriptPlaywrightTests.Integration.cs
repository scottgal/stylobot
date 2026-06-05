using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Integration;

/// <summary>
///     End-to-end Playwright coverage for the modernised client-side detector
///     (the rewritten <c>ClientSide/botdetection.js</c> + the
///     <c>/bot-detection/script.js</c> endpoint introduced in 480b790e).
///     The fixture loads <c>/test/client-script</c> (a minimal page in Demo
///     whose only content is <c>&lt;bot-detection-script /&gt;</c>) in a real
///     headless Chromium, listens for the POST to
///     <c>/bot-detection/fingerprint</c>, and asserts on the payload shape.
///     <para>
///     What we're pinning: the new 2026 collection targets actually arrive on
///     the wire (CDP probe, UA-CH, permissions vector, BroadcastChannel echo,
///     performance.now() clamp residue, Chromium-feature triple, touch
///     profile). If any of these silently stop landing, downstream
///     ClientSideDetector + IdentityVectorContributor lose input -- the same
///     class of silent regression the BdfReplay rig defends for server-side
///     signals.
///     </para>
/// </summary>
[Collection("DemoApp")]
[Trait("Category", "Integration")]
[Trait("Category", "Playwright")]
public class ClientScriptPlaywrightTests : IAsyncLifetime
{
    private readonly string _demoUrl;
    private readonly ITestOutputHelper _output;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public ClientScriptPlaywrightTests(DemoAppFactory factory, ITestOutputHelper output)
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
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"],
        });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task ScriptEndpoint_ServesJavaScript_WithStrongETag()
    {
        // Pure HTTP check -- doesn't need a browser. Pins the contract that
        // /bot-detection/script.js serves the embedded resource verbatim with
        // a content-hash ETag and the right Content-Type.
        using var http = new HttpClient();
        var resp = await http.GetAsync($"{_demoUrl}/bot-detection/script.js");
        Assert.True(resp.IsSuccessStatusCode, $"Expected 2xx, got {(int)resp.StatusCode}");
        Assert.Equal("application/javascript", resp.Content.Headers.ContentType?.MediaType);
        Assert.True((resp.Content.Headers.ContentLength ?? 0) > 5000,
            $"Expected non-trivial body, got {resp.Content.Headers.ContentLength}");
        Assert.NotNull(resp.Headers.ETag);
        Assert.True(resp.Headers.ETag!.Tag.Length > 4, "Expected a real ETag value");

        // Re-request with If-None-Match -- expect 304.
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"{_demoUrl}/bot-detection/script.js");
        req2.Headers.Add("If-None-Match", resp.Headers.ETag!.Tag);
        var resp2 = await http.SendAsync(req2);
        Assert.Equal(304, (int)resp2.StatusCode);
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task TestPage_LoadsBootstrap_AndExternalScript()
    {
        // Bootstrap pair: an inline <script>window.MLBotD={...}</script> +
        // <script src="/bot-detection/script.js">. Playwright sees both as
        // separate document scripts. Verify the network fetch for the
        // external one and the MLBotD global landing on window.
        var ctx = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["ml-bot-test-mode"] = "disable",
            },
        });
        var page = await ctx.NewPageAsync();

        var scriptRequests = new List<string>();
        page.Request += (_, req) =>
        {
            if (req.Url.Contains("/bot-detection/script.js")) scriptRequests.Add(req.Url);
        };

        await page.GotoAsync($"{_demoUrl}/test/client-script",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        Assert.Single(scriptRequests);

        // window.MLBotD published by the bootstrap.
        var hasBootstrap = await page.EvaluateAsync<bool>(
            "() => typeof window.MLBotD === 'object' && typeof window.MLBotD.t === 'string'");
        Assert.True(hasBootstrap, "Expected the bootstrap to publish window.MLBotD with a token");
    }

    [Fact(Skip = "Playwright integration test. Run locally with: dotnet test --filter \"Category=Integration\" after starting Demo manually. See docs/superpowers/plans/2026-06-05-stylobot-observability.md memory: 'project_puppeteer_test_rot'.")]
    public async Task DetectorRuns_BeaconsPayload_WithModern2026Fields()
    {
        // The contract: after loading the test page, the detector script
        // posts a JSON payload to /bot-detection/fingerprint that contains
        // every 2026 collection target (CDP trap, UA-CH, Permissions vector,
        // BroadcastChannel echo, performance.now() clamp residue, Chromium
        // triple, touch profile). Holes mean a probe silently stopped firing.
        //
        // ml-bot-test-mode: disable -- relaxes detection for the page-load
        // request so headless-Chromium markers don't cause Demo to block /
        // challenge the page before the detector script ever runs. The
        // detector itself doesn't need test mode -- the beacon endpoint
        // doesn't gate on detection verdicts.
        var ctx = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["ml-bot-test-mode"] = "disable",
            },
        });
        var page = await ctx.NewPageAsync();

        var fingerprintPostBody = new TaskCompletionSource<string>();
        page.RequestFinished += (_, req) =>
        {
            try
            {
                if (req.Url.Contains("/bot-detection/fingerprint")
                    && string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    var body = req.PostData ?? "";
                    fingerprintPostBody.TrySetResult(body);
                }
            }
            catch { /* never let the listener throw */ }
        };

        // Capture console + page-errors so a failing detector surfaces as a
        // test diagnostic instead of a mute timeout.
        page.Console += (_, msg) => _output.WriteLine($"[browser/{msg.Type}] {msg.Text}");
        page.PageError += (_, err) => _output.WriteLine($"[page-error] {err}");
        page.RequestFailed += (_, req) =>
        {
            if (req.Url.Contains("/bot-detection/") || req.Url.Contains("/test/"))
                _output.WriteLine($"[request-failed] {req.Method} {req.Url}: {req.Failure}");
        };

        // DOMContentLoaded -- the deferred script.js fires AFTER DOM parse.
        // NetworkIdle would wait for the detector's probe fetches (audio +
        // bcast + the beacon POST itself) to all settle, which can exceed
        // Playwright's default 30s on a cold start. The RequestFinished
        // listener catches the beacon as soon as it lands, so we don't
        // actually need NetworkIdle.
        await page.GotoAsync($"{_demoUrl}/test/client-script",
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // The detector waits ~500ms after load to capture interaction signals.
        // Give it 15 seconds in total to fire the beacon -- the detector
        // itself has a 3s collection deadline plus the 500ms interaction
        // window, and external HTTP from the page can be slow on a cold start.
        var bodyTask = await Task.WhenAny(
            fingerprintPostBody.Task,
            Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.True(bodyTask == fingerprintPostBody.Task,
            "Detector script did not POST to /bot-detection/fingerprint within 15s");

        var body = await fingerprintPostBody.Task;
        _output.WriteLine($"POST body length: {body.Length}");
        _output.WriteLine($"POST body (first 800 chars): {body.Substring(0, Math.Min(800, body.Length))}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // === Contract assertions: every 2026 collection target is present ===
        AssertHasProperty(root, "t",         "token");
        AssertHasProperty(root, "v",         "script version");
        AssertHasProperty(root, "ts",        "timestamp");

        // CDP trap (sync probe; field present even if cdpHit=0 on a real browser)
        AssertHasProperty(root, "cdp",       "CDP getter trap");
        AssertHasProperty(root, "stack",     "Error stack foreign-frame probe");

        // UA-CH (Chromium emits userAgentData; the field may be null on
        // non-Chromium browsers but it must EXIST so the server can tell
        // "absent" from "stub").
        AssertHasProperty(root, "ua",        "UA-Client Hints high-entropy values");

        // Permissions API state vector
        AssertHasProperty(root, "perms",     "Permissions API state vector");

        // BroadcastChannel echo latency (median round-trip ms; may be null
        // in sandboxed contexts, must be present)
        AssertHasProperty(root, "bcast",     "BroadcastChannel echo");

        // performance.now() clamp residue
        AssertHasProperty(root, "clamp",     "performance.now() clamp probe");

        // Chromium-feature triple (View Transitions + Speculation Rules + Storage Access)
        AssertHasProperty(root, "triple",    "Chromium-feature presence triple");

        // Headless markers + touch profile
        AssertHasProperty(root, "headless",  "headless markers");
        AssertHasProperty(root, "touch",     "touch profile");

        // Supporting collectors
        AssertHasProperty(root, "basics",    "hardware/locale/preferences");
        AssertHasProperty(root, "legit",     "legitimate-user classifiers (Brave/Lockdown)");
        AssertHasProperty(root, "tail",      "long-tail markers (webdriver/phantom/selenium/iframe)");

        // Sanity-check a couple of well-defined sub-shapes.
        Assert.True(root.GetProperty("stack").TryGetProperty("hasObjectApply", out _),
            "stack.hasObjectApply not present -- the Error-stack foreign-frame probe shape changed");
        Assert.True(root.GetProperty("triple").TryGetProperty("viewTx", out _),
            "triple.viewTx not present -- the Chromium-feature triple shape changed");
        Assert.True(root.GetProperty("touch").TryGetProperty("maxTouch", out _),
            "touch.maxTouch not present");

        // CDP getter trap: in Playwright Chromium (which IS a CDP-driven browser)
        // this SHOULD trip. If it ever returns 0 here, either Playwright
        // changed how it accesses console.debug OR the getter probe broke.
        // Make this an INFORMATIONAL assertion -- emit but don't fail, since
        // browser internals can shift.
        var cdpHit = root.GetProperty("cdp").GetInt32();
        _output.WriteLine($"CDP trap fired on Playwright Chromium: {cdpHit}");

        // navigator.webdriver on Playwright Chromium is true.
        var webdriverFlag = root.GetProperty("tail").GetProperty("webdriver").GetInt32();
        _output.WriteLine($"navigator.webdriver: {webdriverFlag}");
        Assert.Equal(1, webdriverFlag);
    }

    private static void AssertHasProperty(JsonElement obj, string name, string description)
    {
        Assert.True(obj.TryGetProperty(name, out _),
            $"Beacon payload missing top-level '{name}' ({description}) -- " +
            "this probe is part of the 2026 collection contract documented in " +
            "client-side-detection-2026.md. If you deleted the probe, also update " +
            "the contract.");
    }
}
