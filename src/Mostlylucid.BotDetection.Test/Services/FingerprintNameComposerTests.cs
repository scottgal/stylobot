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
    public void Compose_Priority2_ArchetypeName_BeatsFamily()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "Chrome Desktop",
            ["identity.archetype_kind"] = "human-browser",
            ["ua.family"] = "Chrome"
        });

        Assert.StartsWith("Chrome Desktop", name);
    }

    [Fact]
    public void Compose_Priority2_ArchetypeName_DecoratedWithVariance()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "Chrome Desktop",
            ["identity.archetype_kind"] = "human-browser",
            ["identity.drift_top_slot"] = "network.country",
            ["identity.drift_top_category"] = "network",
            ["geo.country_code"] = "JP"
        });

        Assert.Contains("Chrome Desktop", name);
        Assert.Contains("from JP", name);
    }

    [Fact]
    public void Compose_Priority2_HumanAdblockerArchetype_NamesTheVisitor()
    {
        // Adblockers are first-class human-positive identities in this system,
        // so the composer must treat human-adblocker the same way it treats
        // human-browser -- the matched archetype name (uBlock Origin, AdGuard,
        // generic-adblocker) wins the visitor's display name. Without this,
        // a real Chrome + uBlock Origin visitor would render as plain "Chrome"
        // and the entire reason for adding the adblocker roots disappears
        // (the basin exists but never produces a visible name).
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "uBlock Origin",
            ["identity.archetype_kind"] = "human-adblocker",
            ["ua.family"] = "Chrome"
        });

        Assert.StartsWith("uBlock Origin", name);
    }

    [Fact]
    public void Compose_Priority2_BotArchetypeKind_DoesNotName_WhenUaIsNotBot()
    {
        // Naming invariant: a visitor whose UA is a real browser (Priority 1 -- ua.bot_name --
        // did NOT fire) must never be labelled with a bot-shaped archetype name even when the
        // matcher's nearest centroid happens to be a verified-bot family. The bug this guards
        // against: a UK Chrome visitor whose header pattern partially overlaps the Mastodon
        // Family centroid was rendered as "Mastodon Family (header drift)" + Human verdict,
        // which is an impossible combination.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["identity.archetype_name"] = "Mastodon Family",
            ["identity.archetype_kind"] = "verified-bot",
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Mac OS X"
        });

        Assert.DoesNotContain("Mastodon", name ?? string.Empty);
        // Priority 3 short form: "{osShort} {familyShort}" (Mac Chrome).
        // Per user contract 2026-06-18 ("Win Chrome UK is FINE. Chrome on Windows is NOT").
        Assert.Equal("Mac Chrome", name);
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
        Assert.Equal("Lin Firefox", name);
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
    public void Compose_Priority3_FamilyPlusOs_WhenBothAvailable()
    {
        // Priority 3 short form: "{osShort} {familyShort} {distinguisher}".
        // Per user contract 2026-06-18 ("Win Chrome UK is FINE"). The country
        // distinguisher slots into the same place as the previous "(country:sig)"
        // parenthetical -- it's still adding distinguishing signal, just in a
        // space-separated form the dashboard renders without status-as-name look.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
            ["geo.country_code"] = "GB"
        });

        Assert.Contains("Firefox", name);
        Assert.Contains("Lin", name);
        Assert.Contains("GB", name);
    }

    [Fact]
    public void Compose_Priority3_FamilyAlone_WhenOsMissing()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Safari",
            ["geo.country_code"] = "US"
        });

        Assert.StartsWith("Safari", name);
        Assert.DoesNotContain(" on ", name);
    }

    [Fact]
    public void Compose_ReturnsNull_WhenNoUsableSignal()
    {
        // No UA, no archetype, no bot name -- the matcher's signal dict simply lacks enough
        // information to label this visitor. Compose returns null so callers leave the
        // bot_name blank and the dashboard's render layer synthesises a descriptive label
        // from threat / behaviour on the row. Previously this returned "analysing" /
        // "unknown abc123de" which leaked into Top Bots as a literal row name.
        Assert.Null(FingerprintNameComposer.Compose(new Dictionary<string, object>()));
        Assert.Null(FingerprintNameComposer.Compose(
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
    public void Compose_DoesNotAppendSigPrefixOrColonForm_ToName()
    {
        // Operator feedback: baking the signature hash or "(US:abcd)"-style status
        // labels into the name reads as "a status, not a name". The short-form
        // contract DOES include a single-token country distinguisher ("US"), but
        // never a sig-hash prefix and never the parenthetical "(country:hash)" form.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US",
            ["signature.primary"] = "abcd1234efgh5678"
        });

        Assert.Contains("Chrome", name);
        Assert.Contains("Win", name);
        Assert.DoesNotContain("US:", name);
        Assert.DoesNotContain("abcd", name);
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
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object> { ["ua.family"] = "Chrome", ["user_agent.os"] = "Windows" },
            previousName: "analysing");

        Assert.Contains("Chrome", name);
        Assert.Contains("Win", name);
    }

    [Fact]
    public void Compose_ReturnsNull_WhenFreshIsNullAndPreviousIsFallback()
    {
        // Updated contract 2026-06-15: with Priority 4 (raw-UA prefix) now providing a
        // visible last-resort label, hysteresis no longer echoes legacy fallbacks like
        // "analysing" back -- those carry no information and the persist layer should
        // be free to overwrite them with the new UA-prefix shape (or leave blank when
        // even the UA is absent). The previousName-overrides-fresh rule now requires
        // previousName to be a REAL Priority 1-3 name, not another fallback.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            previousName: "analysing");
        Assert.Null(name);
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

    // --- Priority 4: raw UA prefix as last-resort label ---------------------------------

    [Fact]
    public void Compose_ReturnsUaPrefix_WhenNoOtherPriorityHits()
    {
        // User direction 2026-06-15: when bot_name / archetype / family-on-os all miss,
        // showing the raw UA prefix is more useful than returning null.
        //
        // Note: with the claim-first P1 (2026-06-15), a UA whose head matches a YAML
        // bot-pattern entry (e.g. "Mastodon/4.3.0") is now caught by P1 directly --
        // BotPatternLoader.MatchUserAgent scans the raw UA. This test now uses a UA
        // that the catalog deliberately does NOT contain so we can still verify the
        // P4 fallback path.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "NoveltyAgent/9.9.9"
            // no bot_name, no archetype, no family, no os, no YAML hit
        });
        Assert.NotNull(name);
        Assert.StartsWith("NoveltyAgent/", name);
    }

    [Fact]
    public void Compose_UaPrefix_ReadsUserAgentParam_WhenSignalMissing()
    {
        // The matcher hot path passes the UA via the userAgent parameter rather than
        // stuffing it into the signal dict. Priority 4 must self-rescue from that
        // parameter so a brand-new fingerprint isn't anonymous on request 1. Use a UA
        // uap-core does NOT recognise (no curl/wget/etc. shortcut) so Priority 3
        // returns "Other" and we fall through to the raw-UA path.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            userAgent: "MyCustomScanner/1.0 (+https://example.com)");
        Assert.NotNull(name);
        Assert.StartsWith("MyCustomScanner/", name);
    }

    [Fact]
    public void Compose_UaPrefix_Truncates_LongUserAgent()
    {
        // Cap at 48 chars + ellipsis so the dashboard row layout doesn't get blown up
        // by a 500-char enterprise UA (Skype/Outlook/etc. concatenate their entire
        // build chain into the UA string).
        var longUa = new string('A', 200);
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = longUa
        });
        Assert.NotNull(name);
        Assert.True(name.Length <= 49, $"expected length ≤ 49, got {name.Length}: {name}");
        Assert.EndsWith("…", name);
    }

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
    public void Compose_PrefersFamilyOs_OverUaPrefixFallback()
    {
        // Priority 3 must still beat Priority 4. Short-form output.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["ua.raw"] = "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36"
        });
        Assert.Equal("Win Chrome", name);
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
    public void Compose_ClaimFirst_RealChromeUa_WithPrivacyHeaders_StillNamedChromeOnMacOs()
    {
        // Regression: real Chrome with privacy headers must not be mis-claimed. The
        // catalog won't match anything in the UA, so P1 returns null and we fall to
        // P3 (family + OS). The result is the expected browser label, NOT a wrong
        // catalog hit.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36",
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "macOS",
        });

        Assert.NotNull(name);
        Assert.Contains("Chrome", name);
        Assert.Contains("Mac", name);
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
        // P3 (family + OS) takes over, producing the parser's best browser guess.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "MyCustomScanner/1.0",
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
        });

        Assert.NotNull(name);
        Assert.Equal("Lin Firefox", name);
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

    // ============================================================================
    // GetVarianceTerm slot coverage (P9)
    //
    // The variance term is what surfaces inside the "(...)" decoration on the
    // composed name (e.g. "Chrome Desktop (from JP)"). Slots from the identity
    // vector layout that the original switch never named would fall through to
    // the generic "drifted" / category-bucket terms, hiding real signal from
    // operators. Each row below pins one slot to its specific label.
    // ============================================================================

    [Theory]
    [InlineData("transport.tls_ja4",          "TLS shift")]
    [InlineData("transport.h2_settings_hash", "H2 settings shift")]
    [InlineData("transport.alpn",             "ALPN shift")]
    [InlineData("transport.tcp_p0f",          "TCP shift")]
    [InlineData("session.cookie_count",          "cookie state shift")]
    [InlineData("session.has_returning_cookie",  "cookie state shift")]
    [InlineData("session.entry_page_family",     "entry page shift")]
    [InlineData("session.referer_host_family",   "referer shift")]
    [InlineData("session.request_rate",          "rate shift")]
    [InlineData("session.session_age",           "session age shift")]
    [InlineData("session.method_pattern",        "method shift")]
    [InlineData("session.path_entropy",          "path entropy shift")]
    [InlineData("hdr.ua_family",                 "UA family shift")]
    [InlineData("quality.dimension_presence_ratio", "sparse signal")]
    [InlineData("quality.transport_quality",        "low transport quality")]
    [InlineData("quality.cleartext_flag",           "cleartext")]
    [InlineData("quality.layout_version",           "layout version shift")]
    public void GetVarianceTerm_RecognizesPreviouslyUnnamedSlots(string slot, string expected)
    {
        var term = FingerprintNameComposer.GetVarianceTerm(new Dictionary<string, object>
        {
            [SignalKeys.IdentityDriftTopSlot] = slot
        });

        Assert.Equal(expected, term);
    }

    [Theory]
    [InlineData("transport", "transport drift")]
    [InlineData("session",   "session drift")]
    [InlineData("quality",   "quality drift")]
    public void GetVarianceTerm_FallsBackToCategoryLabel_ForUnknownSlot(string category, string expected)
    {
        var term = FingerprintNameComposer.GetVarianceTerm(new Dictionary<string, object>
        {
            // A slot the named-slot switch does not cover, but whose category
            // bucket should still surface a meaningful label rather than the
            // generic "drifted" fallback.
            [SignalKeys.IdentityDriftTopSlot] = $"{category}.unknown_future_slot",
            [SignalKeys.IdentityDriftTopCategory] = category
        });

        Assert.Equal(expected, term);
    }

}
