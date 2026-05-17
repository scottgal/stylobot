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
    public void Compose_Priority4_FingerprintIdPrefix_WhenNoUa()
    {
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            fingerprintId: "abc123def456ghi");

        Assert.Contains("abc123de", name);
    }

    [Fact]
    public void Compose_Priority4_Analysing_WhenNoUaAndNoFingerprintId()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>());
        Assert.Equal("analysing", name);
    }

    [Fact]
    public void Compose_NeverReturnsNullOrEmpty()
    {
        // Every conceivable signal combination must produce a non-empty name. The contract
        // elsewhere is "fingerprints always have a name" — this is the load-bearing invariant.
        foreach (var signals in new[]
        {
            new Dictionary<string, object>(),
            new Dictionary<string, object> { ["ua.bot_name"] = "" },
            new Dictionary<string, object> { ["ua.family"] = "" },
            new Dictionary<string, object> { ["identity.archetype_name"] = "" },
        })
        {
            var name = FingerprintNameComposer.Compose(signals);
            Assert.False(string.IsNullOrWhiteSpace(name), $"got empty for signals: {string.Join(',', signals.Keys)}");
        }
    }

    [Fact]
    public void Compose_UniqueSuffix_AppendsCountryAndSigPrefix()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US",
            ["signature.primary"] = "abcd1234efgh5678"
        });

        Assert.Contains("US:abcd", name);
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
    public void Compose_Hysteresis_FreshWins_WhenBothAreFallback()
    {
        // Both fallback - fresh wins (no information to prefer either way).
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            fingerprintId: "abc123de",
            previousName: "analysing");

        Assert.Contains("unknown abc123de", name);
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

    // --- FirstSeen timestamp: distinguishes fingerprints sharing the same base name -----

    [Fact]
    public void Compose_AppendsFirstSeenTimestamp_WhenProvided()
    {
        // 2025-11-25 13:25 UTC → "251125-1325"
        var ts = new DateTime(2025, 11, 25, 13, 25, 0, DateTimeKind.Utc);
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object> { ["ua.bot_name"] = "Mastodon" },
            firstSeen: ts);

        Assert.Contains("Mastodon 251125-1325", name);
    }

    [Fact]
    public void Compose_FirstSeenTimestamp_DistinguishesIdenticalBaseNames()
    {
        var ts1 = new DateTime(2025, 11, 25, 13, 25, 0, DateTimeKind.Utc);
        var ts2 = new DateTime(2025, 11, 25, 14, 30, 0, DateTimeKind.Utc);
        var signals = new Dictionary<string, object> { ["ua.bot_name"] = "Mastodon" };

        var first = FingerprintNameComposer.Compose(signals, firstSeen: ts1);
        var second = FingerprintNameComposer.Compose(signals, firstSeen: ts2);

        // Two distinct fingerprints both producing "Mastodon" base must differ in display
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compose_FirstSeenTimestamp_NotAppendedToFallback()
    {
        // Priority-4 fallback doesn't go through AppendFirstSeen (the "unknown xxx" prefix
        // already identifies the fingerprint; adding a timestamp would be redundant).
        var ts = new DateTime(2025, 11, 25, 13, 25, 0, DateTimeKind.Utc);
        var name = FingerprintNameComposer.Compose(
            new Dictionary<string, object>(),
            fingerprintId: "abc123de",
            firstSeen: ts);

        Assert.DoesNotContain("251125", name);
    }
}
