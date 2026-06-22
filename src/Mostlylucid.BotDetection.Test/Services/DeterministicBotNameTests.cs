using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Synthesizer contract: every visitor (bot or human) gets a derived display name. Four
///     priorities — known bot name, matched archetype (human-browser only), plain UA family,
///     "Unknown &lt;hex&gt;" cold-state. The bot-only "Automated Bot" composition that used to
///     fire for humans is gone; this file pins the current contract (post-T5 2026-06-22).
/// </summary>
public class DeterministicBotNameTests
{
    private readonly DeterministicBotNameSynthesizer _synthesizer = new();

    [Fact]
    public async Task IsReady_AlwaysTrue()
    {
        Assert.True(_synthesizer.IsReady);
    }

    // ─── Priority 1: known bot name ────────────────────────────────────

    [Theory]
    [InlineData("curl", "curl")]
    [InlineData("Scrapy", "Scrapy")]
    [InlineData("python-requests", "python-requests")]
    [InlineData("Googlebot", "Googlebot")]
    public async Task KnownBotName_UsedDirectly(string botName, string expectedPrefix)
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = botName,
            ["ua.bot_type"] = "Tool"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotNull(name);
        Assert.StartsWith(expectedPrefix, name);
    }

    [Fact]
    public async Task UnknownBotName_FallsThroughToArchetypeOrFamily()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = "unknown", // explicitly the "unknown" sentinel
            ["ua.family"] = "python-requests"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.StartsWith("python-requests", name);
    }

    // ─── Priority 2: archetype name + variance ─────────────────────────

    [Fact]
    public async Task ArchetypeName_UsedAsBase_WhenPresent()
    {
        var signals = new Dictionary<string, object?>
        {
            ["identity.archetype_name"] = "Chrome on Windows",
            ["identity.archetype_kind"] = "human-browser",
            ["ua.family"] = "Chrome",
            ["geo.country_code"] = "US"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotNull(name);
        Assert.StartsWith("Chrome on Windows", name);
        Assert.DoesNotContain("Automated", name);
        Assert.DoesNotContain("Bot", name);
    }

    // Drift variance terms ("from JP", "tooled", "missing client hints",
    // "network drift") were deleted with GetVarianceTerm (T4, 2026-06-22). Tests
    // pinning those parenthetical synthetics violated the display-name contract
    // (parenthesised multi-word labels are a banned shape per
    // FingerprintNameComposerContract) and were removed with this task. Drift
    // now surfaces in its own dashboard column, not in the display name.

    [Fact]
    public async Task ArchetypeName_NoDriftSignal_NoVarianceParenthetical()
    {
        var signals = new Dictionary<string, object?>
        {
            ["identity.archetype_name"] = "Chrome on Windows",
            ["identity.archetype_kind"] = "human-browser"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.StartsWith("Chrome on Windows", name);
        // Unique() may still append "(country:sigprefix)" when geo/sig are present; assert no
        // drift label by checking specific labels rather than parenthesis presence.
        Assert.DoesNotContain("from ", name);
        Assert.DoesNotContain("tooled", name);
        Assert.DoesNotContain("drifted", name);
        Assert.DoesNotContain("missing client hints", name);
    }

    // ─── Priority 3: UA family fallback (Identity off, or first request) ──

    [Fact]
    public async Task Human_GetsFamilyName_WhenNoArchetypeAndNoBotEvidence()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.family"] = "Firefox",
            ["geo.country_code"] = "GB"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotNull(name);
        Assert.StartsWith("Firefox", name);
        Assert.DoesNotContain("Automated", name);
        Assert.DoesNotContain("Bot", name);
    }

    [Fact]
    public async Task Human_NeverNamedAutomatedBot()
    {
        // Regression: previously a Chrome visitor would synthesize as "Automated Bot".
        var signals = new Dictionary<string, object?>
        {
            ["ua.family"] = "Chrome",
            ["ua.bot_name"] = "",
            ["ua.bot_type"] = "",
            ["intent.category"] = "browsing"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotEqual("Automated Bot", name);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"^Automated\s+Bot"), name);
    }

    // ─── Priority 4: cold state ────────────────────────────────────────

    [Fact]
    public async Task EmptySignals_ReturnsUnknownTerminal()
    {
        // Updated contract (T5, 2026-06-22): the composer is the SOLE writer of
        // Fingerprint.DisplayName. Returning null leaks an em-dash placeholder
        // through every downstream reader, which the user called out as unacceptable.
        // With nothing to compose from, the truthful terminal is the canonical
        // "Unknown <hex>" shape -- IsFallback recognises it so a real Priority 1-3
        // name still wins on a later request.
        var signals = new Dictionary<string, object?>();

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.Equal("Unknown 00000000", name);
    }

    // ─── Detailed (name + description) ─────────────────────────────────

    [Fact]
    public async Task SynthesizeDetailed_ReturnsNameAndDescription()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.family"] = "curl",
            ["ua.bot_type"] = "Tool",
            ["intent.category"] = "scanning",
            ["waveform.page_rate"] = 15.0
        };

        var (name, desc) = await _synthesizer.SynthesizeDetailedAsync(signals);

        Assert.NotNull(name);
        Assert.NotNull(desc);
        Assert.Contains("curl", desc, StringComparison.OrdinalIgnoreCase);
    }
}
