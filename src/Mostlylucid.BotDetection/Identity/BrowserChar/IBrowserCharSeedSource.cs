using Mostlylucid.BotDetection.ClientSide;

namespace Mostlylucid.BotDetection.Identity.BrowserChar;

/// <summary>
///     One cold-start seed for the <c>browser_char</c> catalogue. <see cref="Key"/> is
///     the composite <c>{family}:{mode}</c> a request's CLAIMED browser maps to (e.g.
///     <c>chrome:normal</c>); <see cref="SeedCentroid"/> is the expected
///     browser-characteristic vector. Live drift moves the persisted centroid off this
///     prior once observations absorb (slice 2); until then the seed IS the centroid.
/// </summary>
public sealed record BrowserCharSeed(string Key, float[] SeedCentroid);

/// <summary>
///     Source of the cold-start centroid priors for the <c>browser_char</c> catalogue.
///     An interface (mirroring <see cref="BrowserModes.IBrowserModeSeedSource"/>) so a
///     YAML loader can drop in later without touching the catalogue, and tests can swap
///     a fixed table. The catalogue stays oblivious to where the prior comes from.
/// </summary>
public interface IBrowserCharSeedSource
{
    IReadOnlyList<BrowserCharSeed> LoadSeeds();
}

/// <summary>
///     Default seed source (the slice-1 stand-in, exactly like
///     <see cref="BrowserModes.HardcodedBrowserModeSeedSource"/> was before the YAML
///     loader). Encodes each family's prior via <see cref="BrowserCharVectorEncoder"/> so
///     seeds live in the same vector space as live observations.
///
///     <para>
///     The ENGINE identity is version-independent and un-spoofable (v8BreakIterator /
///     Error.captureStackTrace / stack style / userAgentData / showOpenFilePicker), and
///     it is weighted HIGH by the mask -- that is the anchor a spoofer cannot fake. The
///     FEATURE presences are the current-stable expectation, weighted LOW, so a
///     minor-version lag does not false-positive. Privacy modes (Brave / Firefox-RFP /
///     Tor / Lockdown) are handled by the atom failing open before scoring, so only the
///     <c>:normal</c> key per family is seeded here.
///     </para>
/// </summary>
public sealed class HardcodedBrowserCharSeedSource : IBrowserCharSeedSource
{
    private readonly IdentityVectorLayout _layout;
    private readonly IReadOnlyList<BrowserCharSeed> _seeds;

    public HardcodedBrowserCharSeedSource(IdentityVectorLayout layout)
    {
        _layout = layout;
        _seeds = Build();
    }

    public IReadOnlyList<BrowserCharSeed> LoadSeeds() => _seeds;

    private IReadOnlyList<BrowserCharSeed> Build() => new List<BrowserCharSeed>
    {
        Seed("chrome:normal", Chromium()),
        Seed("edge:normal", Chromium()),
        Seed("firefox:normal", Firefox()),
        Seed("safari:normal", Safari()),
    };

    private BrowserCharSeed Seed(string key, (FeaturesBlock f, TripleBlock t, EngineBlock e) spec)
        => new(key, BrowserCharVectorEncoder.Encode(_layout, spec.f, spec.t, spec.e));

    // Chromium (Chrome / Edge): V8 engine, full modern feature surface.
    private static (FeaturesBlock, TripleBlock, EngineBlock) Chromium() => (
        new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = 1 },
        new TripleBlock { HasViewTransition = 1, HasSpeculationRules = 1, HasStorageAccess = 1 },
        new EngineBlock
        {
            V8BreakIterator = 1, ErrorCaptureStackTrace = 1, RegexLookbehind = 1,
            ShowOpenFilePicker = 1, UserAgentData = 1, StackStyle = "v8",
        });

    // Firefox (Gecko / SpiderMonkey): no V8 internals, no UA-CH, no File System Access,
    // no View Transitions / Speculation Rules on stable.
    private static (FeaturesBlock, TripleBlock, EngineBlock) Firefox() => (
        new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = 0 },
        new TripleBlock { HasViewTransition = 0, HasSpeculationRules = 0, HasStorageAccess = 1 },
        new EngineBlock
        {
            V8BreakIterator = 0, ErrorCaptureStackTrace = 0, RegexLookbehind = 1,
            ShowOpenFilePicker = 0, UserAgentData = 0, StackStyle = "spidermonkey-jsc",
        });

    // Safari (WebKit / JavaScriptCore): JSC emits @-style stacks our probe classifies as
    // spidermonkey-jsc; no V8 internals, no UA-CH, no File System Access; View Transitions
    // on Safari 18 but no Speculation Rules.
    private static (FeaturesBlock, TripleBlock, EngineBlock) Safari() => (
        new FeaturesBlock { Popover = 1, CssHas = 1, ArrayFindLast = 1, StructuredClone = 1, WebGpu = 0 },
        new TripleBlock { HasViewTransition = 1, HasSpeculationRules = 0, HasStorageAccess = 1 },
        new EngineBlock
        {
            V8BreakIterator = 0, ErrorCaptureStackTrace = 0, RegexLookbehind = 1,
            ShowOpenFilePicker = 0, UserAgentData = 0, StackStyle = "spidermonkey-jsc",
        });
}
