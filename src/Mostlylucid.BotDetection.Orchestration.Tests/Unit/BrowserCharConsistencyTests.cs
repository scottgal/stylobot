using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserChar;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     The payoff: browser-characteristic claim verification. A session claiming Chrome
///     while running the wrong engine (SpiderMonkey/JSC) lands high drift; a genuine
///     browser (engine matches the claim) is consistent; an unknown family fails open.
///     The engine dims are weighted high so a perfect feature-spoof cannot hide the
///     engine mismatch.
/// </summary>
public class BrowserCharConsistencyTests
{
    private static readonly IdentityVectorLayout Layout = IdentityVectorLayout.DefaultV1();

    private static BrowserCharConsistencyScorer Scorer() =>
        BrowserCharConsistencyScorer.FromSeeds(
            new HardcodedBrowserCharSeedSource(Layout).LoadSeeds(),
            Layout, engineWeight: 3.0f, featureWeight: 0.5f);

    private static float[] Observe(string stackStyle, int v8, int uaData, int showPicker,
        int viewTx, int webGpu) =>
        BrowserCharVectorEncoder.Encode(Layout,
            new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = webGpu },
            new TripleBlock { HasViewTransition = viewTx, HasSpeculationRules = viewTx, HasStorageAccess = 1 },
            new EngineBlock
            {
                V8BreakIterator = v8, ErrorCaptureStackTrace = v8, RegexLookbehind = 1,
                ShowOpenFilePicker = showPicker, UserAgentData = uaData, StackStyle = stackStyle,
            });

    [Fact]
    public void ChromeClaim_WithV8Engine_IsConsistent()
    {
        // Genuine Chrome: V8 internals + UA-CH + File System Access + full features.
        var obs = Observe(stackStyle: "v8", v8: 1, uaData: 1, showPicker: 1, viewTx: 1, webGpu: 1);
        var score = Scorer().Score("chrome:normal", obs);

        Assert.True(score.KeyFound);
        Assert.True(score.Drift < 0.15, $"genuine Chrome should be consistent; drift={score.Drift:F3}");
    }

    [Fact]
    public void ChromeClaim_ButSpiderMonkeyEngine_IsInconsistent()
    {
        // Spoofer: UA says Chrome, but the engine is SpiderMonkey/JSC and the V8-only /
        // Chromium-only tells are absent. The engine-weighted mask makes this land hard.
        var obs = Observe(stackStyle: "spidermonkey-jsc", v8: 0, uaData: 0, showPicker: 0, viewTx: 0, webGpu: 0);
        var score = Scorer().Score("chrome:normal", obs);

        Assert.True(score.KeyFound);
        Assert.True(score.Drift > 0.5, $"Chrome-claim over a non-V8 engine must be inconsistent; drift={score.Drift:F3}");
    }

    [Fact]
    public void FirefoxClaim_WithSpiderMonkey_IsConsistent_NoFalsePositive()
    {
        // Real Firefox: SpiderMonkey, no V8 internals, no UA-CH -> must NOT false-positive.
        var obs = Observe(stackStyle: "spidermonkey-jsc", v8: 0, uaData: 0, showPicker: 0, viewTx: 0, webGpu: 0);
        var score = Scorer().Score("firefox:normal", obs);

        Assert.True(score.KeyFound);
        Assert.True(score.Drift < 0.15, $"real Firefox should be consistent; drift={score.Drift:F3}");
    }

    [Fact]
    public void UnknownFamily_FailsOpen()
    {
        var obs = Observe(stackStyle: "v8", v8: 1, uaData: 1, showPicker: 1, viewTx: 1, webGpu: 1);
        var score = Scorer().Score("opera:normal", obs);

        Assert.False(score.KeyFound);
        Assert.Equal(0, score.Drift);
    }
}
