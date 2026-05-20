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
            ["identity.drift_top_slot"] = "network.country",
            ["identity.drift_top_category"] = "network",
            ["geo.country_code"] = "JP"
        });

        Assert.Contains("Chrome Desktop", name);
        Assert.Contains("from JP", name);
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
    public void Compose_ReturnsPrevious_WhenFreshIsNullAndPreviousIsFallback()
    {
        // Compose no longer returns "analysing" or "unknown xxx" itself; it returns null.
        // If the caller supplies a legacy fallback as previousName, we still echo it back
        // (hysteresis: don't blank a name out, even a poor one) until a better fresh result
        // arrives. The matcher's IsFallback check at the persist site keeps fallbacks out
        // of the DB; this just preserves what's already there during the request.
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            previousName: "analysing");
        Assert.Equal("analysing", name);
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

}
