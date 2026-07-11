using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserChar;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Signal-flow probe for <c>browser.characteristic_drift</c> (overview's pulled-forward
///     Rule-4 follow-up). Pins that the signal is in the prod policy allow-list (or it silently
///     drops under DetectionPolicy.Default while working in the demo), and that the atom actually
///     raises it + contributes asymmetrically under a realistic HttpContext.
/// </summary>
public class BrowserCharConsistencyAtomProbeTests
{
    private static readonly IdentityVectorLayout Layout = IdentityVectorLayout.DefaultV1();

    [Fact]
    public void BrowserCharConsistency_IsInDefaultPolicy_NotDarkInProd()
    {
        // Rule 4: a production signal must be in the Default policy's fast-path set. The
        // demo runs all detectors, so an omission here only shows up in prod.
        Assert.Contains("BrowserCharConsistency", DetectionPolicy.Default.FastPathDetectors);
    }

    private static (BrowserCharConsistencyAtom atom, SignalSink sink) Setup(
        EngineBlock engine, FeaturesBlock features, TripleBlock triple, string uaFamily)
    {
        var fp = new BrowserFingerprintResult { Engine = engine, Features = features, Triple = triple };
        var ctx = new DefaultHttpContext();
        ctx.Items["__mlbotd_fingerprint"] = fp;
        var accessor = new HttpContextAccessor { HttpContext = ctx };

        var scorer = BrowserCharConsistencyScorer.FromSeeds(
            new HardcodedBrowserCharSeedSource(Layout).LoadSeeds(), Layout, 3.0f, 0.5f);
        var opts = new BotDetectionOptions();
        opts.Identity.Enabled = true;
        opts.Identity.BrowserChar.Enabled = true;

        var atom = new BrowserCharConsistencyAtom(
            NullLogger<BrowserCharConsistencyAtom>.Instance, scorer, Layout, accessor, Options.Create(opts));

        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        sink.Raise($"{SignalKeys.UserAgentFamily}:{uaFamily}", "session-1");
        return (atom, sink);
    }

    [Fact]
    public async Task SpoofedEngine_RaisesDriftSignal_AndSuspicionContribution()
    {
        // UA claims Chrome, but the engine is SpiderMonkey/JSC -> inconsistent.
        var (atom, sink) = Setup(
            new EngineBlock { StackStyle = "spidermonkey-jsc", V8BreakIterator = 0, ErrorCaptureStackTrace = 0, RegexLookbehind = 1, ShowOpenFilePicker = 0, UserAgentData = 0 },
            new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = 0 },
            new TripleBlock { HasViewTransition = 0, HasSpeculationRules = 0, HasStorageAccess = 1 },
            uaFamily: "Chrome");

        var contributions = await atom.DetectAsync(sink, "session-1");

        // The signal flowed onto the sink (this is the "not silently dropped" pin)...
        Assert.NotNull(sink.ReadHint(SignalKeys.BrowserCharacteristicDrift));
        // ...and it RAISED suspicion (positive delta, asymmetric).
        Assert.Single(contributions);
        Assert.True(contributions[0].ConfidenceDelta > 0, "engine spoof must raise, not lower");
    }

    [Fact]
    public async Task ConsistentEngine_RaisesDriftSignal_ButNoContribution()
    {
        // Genuine Chrome: V8 engine -> consistent -> the signal still flows (observability)
        // but there is NO contribution (consistency is neutral, never a human discount).
        var (atom, sink) = Setup(
            new EngineBlock { StackStyle = "v8", V8BreakIterator = 1, ErrorCaptureStackTrace = 1, RegexLookbehind = 1, ShowOpenFilePicker = 1, UserAgentData = 1 },
            new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = 1 },
            new TripleBlock { HasViewTransition = 1, HasSpeculationRules = 1, HasStorageAccess = 1 },
            uaFamily: "Chrome");

        var contributions = await atom.DetectAsync(sink, "session-1");

        Assert.NotNull(sink.ReadHint(SignalKeys.BrowserCharacteristicDrift));
        Assert.Empty(contributions);
    }
}
