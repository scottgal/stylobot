using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     The composer is the single source of truth for fingerprint display names. These tests
///     pin the four-priority contract and the "never returns empty" invariant the matcher
///     persists onto every Fingerprint row.
/// </summary>
public class FingerprintNameComposerTests
{
    [Fact]
    public void Compose_Priority1_KnownBotName()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Googlebot",
            ["ua.family"] = "Googlebot"
        });

        Assert.StartsWith("Googlebot", name);
    }

    [Fact]
    public void Compose_ArchetypeName_DoesNotEnterName()
    {
        // Per the 2026-06-26 contract restore: archetype names are inferred labels
        // and belong to the drift-badge column, NEVER the name. The name is a
        // projection from directly-observed signals only.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "Chrome Desktop",
            ["identity.archetype_kind"] = "human-browser",
            ["ua.family"] = "Chrome"
        });

        Assert.DoesNotContain("Desktop", name ?? string.Empty);
        Assert.Equal("Chrome", name);
    }

    [Fact]
    public void Compose_AdblockerArchetype_DoesNotEnterName_ButObservedModifierDoes()
    {
        // Inferred adblocker archetype is OUT (it's a centroid match, not an
        // observed signal). The "+ uBlock" only appears when the directly-observed
        // presentation.has_ublock signal is true -- that's the architectural
        // distinction: observed vs inferred.
        var nameNoSignal = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "uBlock Origin",
            ["identity.archetype_kind"] = "human-adblocker",
            ["ua.family"] = "Chrome"
        });
        Assert.DoesNotContain("uBlock", nameNoSignal ?? string.Empty);
        Assert.Equal("Chrome", nameNoSignal);

        var nameWithObservedSignal = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "uBlock Origin",
            ["identity.archetype_kind"] = "human-adblocker",
            ["ua.family"] = "Chrome",
            ["presentation.has_ublock"] = true,
        });
        Assert.Contains("+ uBlock", nameWithObservedSignal ?? string.Empty);
    }

    [Fact]
    public void Compose_BotArchetypeKind_DoesNotName_WhenUaIsNotBot()
    {
        // Per the 2026-06-26 contract restore: rich projection (family + os) for
        // a real Chrome visitor that grazes a bot-shaped archetype centroid.
        // The archetype is IGNORED; the name reflects what the fingerprint
        // actually looks like.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "Mastodon Family",
            ["identity.archetype_kind"] = "verified-bot",
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Mac OS X"
        });

        Assert.DoesNotContain("Mastodon", name ?? string.Empty);
        Assert.Equal("Chrome Mac OS X", name);
    }

    [Fact]
    public void Compose_Priority2_ToolArchetypeKind_DoesNotName_WhenUaIsNotBot()
    {
        // Same invariant for tool-shaped archetypes (curl, python-requests). A real Firefox
        // visitor must not be labelled "python-requests" because their fingerprint vector
        // grazed that centroid.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "python-requests",
            ["identity.archetype_kind"] = "tool",
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux"
        });

        Assert.DoesNotContain("python", name ?? string.Empty);
        Assert.Equal("Firefox Linux", name);
    }

    [Fact]
    public void Compose_Priority1_SelfDeclaredBot_StillUsesBotName_RegardlessOfArchetypeKind()
    {
        // Self-declared bots (UA carries "Mastodon/4.x" etc.) keep Priority 1 -- the
        // archetype kind gate only applies at Priority 2, after Priority 1 has already
        // returned. A genuine Mastodon instance still renders as "Mastodon ...".
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Mastodon",
            ["ua.family"] = "Mastodon",
            ["identity.archetype_name"] = "Mastodon Family",
            ["identity.archetype_kind"] = "verified-bot"
        });

        Assert.StartsWith("Mastodon", name);
    }

    [Fact]
    public void Compose_Priority3_ProjectsFamilyAndOs()
    {
        // 2026-06-26 contract restore: Priority 3 projects family + os (+ version,
        // os_version, observed modifiers when present). Geo / archetype / signature
        // signals never enter the name.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
            ["geo.country_code"] = "GB"
        });

        Assert.Equal("Firefox Linux", name);
    }

    [Fact]
    public void Compose_Priority3_FamilyAlone_WhenOsMissing()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Safari",
            ["geo.country_code"] = "US"
        });

        Assert.Equal("Safari", name);
    }

    [Fact]
    public void Compose_ReturnsUnknownTerminal_WhenNoUsableSignal()
    {
        // No UA, no archetype, no bot name -- the matcher's signal dict simply lacks enough
        // information to label this visitor. Under "Unknown is not a valid state" (2026-07-30)
        // the terminal synthesises what we DO know: with nothing at all, "Unclassified Client";
        // with a fingerprint id, "Client <hex>". Never the word "Unknown". IsFallback still
        // recognises both so a real Priority 1-3 name later wins via hysteresis.
        Assert.Equal("Unclassified Client",
            FingerprintNameComposer.Compose(new Dictionary<string, object>()));
        Assert.Equal("Client abc123de",
            FingerprintNameComposer.Compose(
                new Dictionary<string, object>(),
                fingerprintId: "abc123def456ghi"));
    }

    [Fact]
    public void Compose_PreservesPreviousName_WhenNoFreshSignal()
    {
        // Hysteresis: when current request has nothing usable but a real previousName exists,
        // keep the previous one. Prevents "Chrome" -> null -> "Chrome" churn when the matcher
        // runs before UserAgentContributor on a hot path.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            previousName: "Chrome on Windows");
        Assert.Equal("Chrome on Windows", name);
    }

    [Fact]
    public void Compose_DoesNotLeakSigPrefixOrColonForm_IntoName()
    {
        // 2026-06-26 contract: name projects from observed family + os only; geo
        // and signature signals never leak in regardless of what's in the dict.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US",
            ["signature.primary"] = "abcd1234efgh5678"
        });

        Assert.Equal("Chrome Windows", name);
    }

    [Fact]
    public void Compose_Priority1_AppendsInstanceDiscriminator_FromFediverseUa()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Mastodon",
            ["ua.raw"] = "http.rb/5.2 (Mastodon/4.2.1; +https://mastodon.social/)"
        });

        Assert.Contains("Mastodon mastodon.social", name);
    }

    [Fact]
    public void Compose_Priority1_NoDiscriminator_ForVendorHomeUrl()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "GPTBot",
            ["ua.raw"] = "Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)"
        });

        // No discriminator suffix - openai.com is a vendor-home reference, not an instance
        Assert.StartsWith("GPTBot", name);
        Assert.DoesNotContain("openai.com", name);
    }

    [Fact]
    public void Compose_Priority1_AppendsDeceptiveMarker_WhenSpoofedClaim()
    {
        // UA says Googlebot but VerifiedBotContributor flagged the IP as spoofed
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Googlebot",
            ["verifiedbot.spoofed"] = true
        });

        Assert.Contains("Googlebot", name);
        Assert.Contains("(!)", name);
    }

    [Fact]
    public void Compose_Priority1_AppendsDeceptiveMarker_OnRdnsMismatch()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Bingbot",
            ["verifiedbot.rdns_mismatch"] = true
        });

        Assert.Contains("(!)", name);
    }

    [Fact]
    public void Compose_Priority1_NoDeceptiveMarker_WhenVerified()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Googlebot",
            ["verifiedbot.confirmed"] = true,
            ["verifiedbot.spoofed"] = false
        });

        Assert.DoesNotContain("(!)", name);
    }

    // --- Hysteresis: never let Priority-4 fallback replace a real previous name ---------

    [Fact]
    public void Compose_Hysteresis_PreviousNameWins_WhenFreshIsAnalysing()
    {
        // No UA, no signals - fresh would be Priority-4 "analysing". Previous was real.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            fingerprintId: null,
            userAgent: null,
            previousName: "Chrome on Windows");

        Assert.Equal("Chrome on Windows", name);
    }

    [Fact]
    public void Compose_Hysteresis_PreviousNameWins_WhenFreshIsUnknownIdPrefix()
    {
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            fingerprintId: "abc123de4567",
            userAgent: null,
            previousName: "Firefox on Linux");

        Assert.Equal("Firefox on Linux", name);
    }

    [Fact]
    public void Compose_Hysteresis_FreshWins_WhenItIsNotFallback()
    {
        // Fresh has real signals - we upgrade past the previous "analysing".
        // 2026-06-26: Priority 3 projects family + os.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object> { ["ua.family"] = "Chrome", ["user_agent.os"] = "Windows" },
            previousName: "analysing");

        Assert.Equal("Chrome Windows", name);
    }

    [Fact]
    public void Compose_ReturnsUnknownTerminal_WhenFreshDegeneratesAndPreviousIsFallback()
    {
        // Hysteresis only kicks in when previousName is a REAL Priority 1-3 name, not
        // another fallback. With "analysing" (a fallback) as previousName and no signals
        // to feed a fresh real name, the terminal synthesises "Unclassified Client"
        // (2026-07-30, "Unknown is not a valid state") rather than echoing the stale
        // fallback. The matcher's persist layer is then free to overwrite "analysing" on
        // disk with the new terminal (or a real name on a later request).
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            previousName: "analysing");
        Assert.Equal("Unclassified Client", name);
    }

    [Fact]
    public void IsFallback_RecognisesAnalysingAndUnknownPrefix()
    {
        Assert.True(FingerprintNameComposer.IsFallback("analysing"));
        Assert.True(FingerprintNameComposer.IsFallback("analysing (US:abcd)"));
        Assert.True(FingerprintNameComposer.IsFallback("unknown abc123de"));
        Assert.True(FingerprintNameComposer.IsFallback("unknown abc123de (US:abcd)"));
        Assert.False(FingerprintNameComposer.IsFallback("Chrome on Windows"));
        Assert.False(FingerprintNameComposer.IsFallback("Chrome on Windows (US:abcd)"));
        Assert.False(FingerprintNameComposer.IsFallback("Mastodon mastodon.social"));
    }

    // --- Priority 4: defensive Unknown <hex> terminal ----------------------------------
    //
    // T5 (2026-06-22): Priority 4 is no longer a visible UA prefix. The defensive
    // contract gate at the end of ComposeFresh routes any non-conforming output
    // (including the raw UA prefix path) to the canonical "Unknown <hex>" shape so
    // we never emit a multi-token or "/"-bearing display name. Tests that pinned
    // the raw-UA-prefix output ("NoveltyAgent/9.9.9", "MyCustomScanner/...", 48-char
    // truncation with ellipsis) were deleted with this task: they pinned a banned
    // shape, which the contract test fixture catches and rejects upstream.

    [Fact]
    public void Compose_PrefersBotName_OverUaPrefixFallback()
    {
        // Priority 1 must still beat Priority 4. Googlebot UAs have bot_name set.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.bot_name"] = "Googlebot",
            ["ua.raw"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"
        });
        Assert.NotNull(name);
        Assert.StartsWith("Googlebot", name);
        Assert.DoesNotContain("Mozilla/", name);
    }

    [Fact]
    public void Compose_PrefersFamily_OverUaPrefixFallback()
    {
        // Priority 2 (family projection) must beat the last-resort Unknown terminal, and
        // the raw UA must never leak in as the name. Short form now: family + OS name, no
        // OS *version* (the "10" was dropped 2026-07-10 to keep list names short).
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["ua.raw"] = "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36"
        });
        Assert.Equal("Chrome Windows", name);
    }

    [Fact]
    public void IsFallback_ReturnsTrue_ForUaPrefixLabel()
    {
        // The UA-prefix Priority 4 output IS a fallback -- if a real Priority 1-3 name
        // later becomes available we want it to win. Detection: every UA string carries
        // a "/" (Mozilla/5.0, Mastodon/4.3.0, curl/8.0). Real composed names from P1-P3
        // never do ("Googlebot", "Chrome on Windows", "Mastodon mastodon.social").
        Assert.True(FingerprintNameComposer.IsFallback("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleW…"));
        Assert.True(FingerprintNameComposer.IsFallback("Mastodon/4.3.0 (+https://m…"));
        Assert.True(FingerprintNameComposer.IsFallback("curl/8.0.1"));
        Assert.True(FingerprintNameComposer.IsFallback("Mastodon/4.3.0"));
        // Sanity: real names still aren't fallbacks.
        Assert.False(FingerprintNameComposer.IsFallback("Chrome on Windows"));
        Assert.False(FingerprintNameComposer.IsFallback("Mastodon mastodon.social"));
        Assert.False(FingerprintNameComposer.IsFallback("Googlebot"));
    }

    [Fact]
    public void Compose_PreservesPreviousRealName_OverFreshUaPrefix()
    {
        // Load-bearing hysteresis test: with the new Priority 4 fallback "fresh" is no
        // longer null when a raw UA is present, but a previously-persisted REAL name
        // (Priority 1-3) must still win. Otherwise "Googlebot" would flicker back to
        // "Mozilla/5.0..." on the next request when bot_name happened not to be in
        // the signal dict.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>
            {
                ["ua.raw"] = "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36"
            },
            previousName: "Googlebot");
        Assert.Equal("Googlebot", name);
    }

    // ----- Claim-first composition (user directive 2026-06-15) ----------------------------
    // "We always start with what is claimed and TEST IT. Not start with a match, use that
    // and ignore what's claimed."
    //
    // Rewires P1 from "trust the cached ua.bot_name signal" to "extract the claim straight
    // from the raw UA via the YAML bot-patterns catalog". The catalog match is deterministic
    // and runs even when the matcher fired before UserAgentContributor (matcher Priority 6,
    // UA contributor Priority 10). Then run verification in parallel; if a Verified bot
    // claim came back spoofed, mark with " (!)".

    [Fact]
    public void Compose_ClaimFirst_MastodonUa_WithCentroidDrift_StillNamedMastodon()
    {
        // The bug. Mastodon UA wrapped in http.rb; centroid drifted onto a chrome-with-
        // privacy-headers archetype so ua.family signal says "Chrome", ua.os says "macOS",
        // and the drift slot says "hdr.upgrade_insecure_requests". ua.bot_name is absent
        // (matcher fired before UserAgentContributor populated bot_name). Result today:
        // "Chrome on macOS (privacy headers)". Required: "Mastodon" via raw-UA catalog
        // claim extraction.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "http.rb/5.x.x (Mastodon/4.3.0; +https://mastodon.social/)",
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "macOS",
            ["identity.drift_top_slot"] = "hdr.upgrade_insecure_requests",
            // No ua.bot_name -- this is the actual staging condition
        });

        Assert.NotNull(name);
        Assert.Contains("Mastodon", name);
        Assert.DoesNotContain("Chrome", name);
    }

    [Fact]
    public void Compose_ClaimFirst_SpoofedGooglebotUa_GetsSpoofedMarker()
    {
        // UA claims Googlebot. ua.bot_name not in signals (race-loss path). IP-range
        // verification disagrees: verifiedbot.spoofed = true. Composer must (a) extract
        // the Googlebot claim straight from the UA string, AND (b) append the spoofed
        // marker. The operator wants to see the claim AND the verdict, never one or
        // the other alone.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            ["verifiedbot.spoofed"] = true,
        });

        Assert.NotNull(name);
        Assert.Contains("Googlebot", name);
        Assert.EndsWith(" (!)", name);
    }

    [Fact]
    public void Compose_ClaimFirst_RealChromeUa_WithPrivacyHeaders_StillNamedChrome()
    {
        // Regression: real Chrome with privacy headers must not be mis-claimed. The
        // catalog won't match anything in the UA, so P1 returns null and we fall to
        // P3 (rich projection per 2026-06-26 contract).
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "macOS",
        });

        Assert.StartsWith("Chrome", name);
        Assert.Contains("macOS", name);
    }

    [Fact]
    public void Compose_ClaimFirst_RawUaMatchesYamlCatalog_WhenBotNameSignalAbsent()
    {
        // Generalised claim-first: even without ua.bot_name in signals, the raw UA
        // string is scanned via BotPatternLoader.MatchUserAgent. The fediverse YAML
        // entry for "Mastodon" produces a hit.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "http.rb/5.2 (Mastodon/4.2.1; +https://mastodon.social/)",
        });

        Assert.NotNull(name);
        Assert.Contains("Mastodon", name);
        // Discriminator suffix from UserAgentDiscriminator
        Assert.Contains("mastodon.social", name);
    }

    [Fact]
    public void Compose_ClaimFirst_GptbotRawUa_NamedFromYamlEvenWithoutBotNameSignal()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mozilla/5.0 (compatible; GPTBot/1.0; +https://openai.com/gptbot)",
        });

        Assert.NotNull(name);
        Assert.StartsWith("GPTBot", name);
    }

    [Fact]
    public void Compose_ClaimFirst_CurlRawUa_NamedFromYaml()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "curl/8.0.1",
        });

        Assert.NotNull(name);
        // YAML curl entry uses bot_name "curl"
        Assert.StartsWith("curl", name);
        // The raw-UA-prefix Priority 4 fallback would render "curl/8.0.1" which is also
        // acceptable visually but our intent is to fire P1 via the catalog. Either way
        // the name starts with "curl".
    }

    [Fact]
    public void Compose_ClaimFirst_UnknownUa_FallsThroughToP3()
    {
        // The catalog has no entry for a totally novel UA. We must NOT invent a P1 claim.
        // P3 takes over with rich projection (2026-06-26 contract).
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "MyCustomScanner/1.0",
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
        });

        Assert.Equal("Firefox Linux", name);
    }

    [Fact]
    public void Compose_ClaimFirst_VerifiedClaim_NoSpoofMarker()
    {
        // Negative case: claim + verification PASSED. No marker.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
            ["verifiedbot.confirmed"] = true,
            ["verifiedbot.spoofed"] = false,
        });

        Assert.NotNull(name);
        Assert.Contains("Googlebot", name);
        Assert.DoesNotContain("(!)", name);
    }

    // ----- Claim-verify-trust gap #2: composer reads ua.bot_instance signal -------------
    // When UserAgentContributor has already populated ua.bot_instance for this request,
    // the composer must prefer the cached signal over re-extracting via the helper.
    // The direct-extraction fallback only fires when the signal is absent (matcher
    // running on the hot path before UserAgentContributor).

    [Fact]
    public void Compose_Reads_BotInstance_Signal_When_Present()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            [SignalKeys.UserAgentBotName] = "Mastodon",
            [SignalKeys.UserAgentBotInstance] = "mas.to",
            [SignalKeys.UserAgent] = "http.rb (Mastodon/4.x; +https://mas.to/)",
        });

        Assert.NotNull(name);
        Assert.Equal("Mastodon mas.to", name);
    }

    // ----- 2026-06-26 contract restore tests --------------------------------------------

    [Fact]
    public void Compose_Projection_AppendsMajorVersion_WhenFamilyVersionSignalPresent()
    {
        // Short form (2026-07-10): the MAJOR version only (149, not 149.0.0) + OS name,
        // so a fleet of Chrome visitors stays distinct without a 30-char list name.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["ua.family_version"] = "149.0.0",
            ["user_agent.os"] = "macOS",
        });
        Assert.Equal("Chrome 149 macOS", name);
    }

    [Fact]
    public void Compose_Projection_AppendsObservedUblockModifier()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "macOS",
            ["presentation.has_ublock"] = true,
        });
        Assert.Equal("Chrome macOS + uBlock", name);
    }

    [Fact]
    public void Compose_Projection_AppendsMultipleObservedModifiers()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
            ["presentation.has_ublock"] = true,
            ["transport.is_tor"] = true,
        });
        Assert.Equal("Firefox Linux + uBlock + Tor", name);
    }

    [Fact]
    public void Compose_Projection_OmitsModifiers_WhenSignalsAreFalse()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["presentation.has_ublock"] = false,
            ["transport.is_tor"] = false,
        });
        Assert.Equal("Chrome Windows", name);
    }

}
