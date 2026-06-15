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
        Assert.Equal("Chrome on Mac OS X", name);
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
        Assert.Equal("Firefox on Linux", name);
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
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Firefox",
            ["user_agent.os"] = "Linux",
            ["geo.country_code"] = "GB"
        });

        Assert.Contains("Firefox on Linux", name);
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
    public void Compose_DoesNotAppendCountryOrSigPrefix_ToName()
    {
        // The dashboard renders country and signature in their own columns; baking them
        // into the name produced labels like "Chrome (US:abcd)" that read as "a status,
        // not a name" (operator feedback). The name should now be just "Chrome on Windows".
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US",
            ["signature.primary"] = "abcd1234efgh5678"
        });

        Assert.Equal("Chrome on Windows", name);
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

        Assert.Contains("Chrome on Windows", name);
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
        // showing the raw UA prefix is more useful than returning null. Mastodon/4.3.0
        // is exactly the case -- the UA carries no +URL discriminator so P1 doesn't
        // fire, uap-core categorises it as "Other" so P3 doesn't fire, but the operator
        // can still see what was sent if we surface the head of the UA.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.raw"] = "Mastodon/4.3.0"
            // no bot_name, no archetype, no family, no os
        });
        Assert.NotNull(name);
        Assert.StartsWith("Mastodon/", name);
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
        // Priority 3 must still beat Priority 4.
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["ua.raw"] = "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36"
        });
        Assert.Equal("Chrome on Windows", name);
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

}
