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
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
        {
            ["ua.bot_name"] = "Googlebot",
            ["ua.family"] = "Googlebot"
        });

        Assert.StartsWith("Googlebot", name);
    }

    [Fact]
    public void Compose_Priority2_ArchetypeName_BeatsFamily()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
        {
            ["identity.archetype_name"] = "Chrome Desktop",
            ["ua.family"] = "Chrome"
        });

        Assert.StartsWith("Chrome Desktop", name);
    }

    [Fact]
    public void Compose_Priority2_ArchetypeName_DecoratedWithVariance()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
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
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
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
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
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
            new Dictionary<string, object?>(),
            fingerprintId: "abc123def456ghi");

        Assert.Contains("abc123de", name);
    }

    [Fact]
    public void Compose_Priority4_Analysing_WhenNoUaAndNoFingerprintId()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>());
        Assert.Equal("analysing", name);
    }

    [Fact]
    public void Compose_NeverReturnsNullOrEmpty()
    {
        // Every conceivable signal combination must produce a non-empty name. The contract
        // elsewhere is "fingerprints always have a name" — this is the load-bearing invariant.
        foreach (var signals in new[]
        {
            new Dictionary<string, object?>(),
            new Dictionary<string, object?> { ["ua.bot_name"] = "" },
            new Dictionary<string, object?> { ["ua.family"] = "" },
            new Dictionary<string, object?> { ["identity.archetype_name"] = "" },
        })
        {
            var name = FingerprintNameComposer.Compose(signals);
            Assert.False(string.IsNullOrWhiteSpace(name), $"got empty for signals: {string.Join(',', signals.Keys)}");
        }
    }

    [Fact]
    public void Compose_UniqueSuffix_AppendsCountryAndSigPrefix()
    {
        var name = FingerprintNameComposer.Compose(new Dictionary<string, object?>
        {
            ["ua.family"] = "Chrome",
            ["user_agent.os"] = "Windows",
            ["geo.country_code"] = "US",
            ["signature.primary"] = "abcd1234efgh5678"
        });

        Assert.Contains("US:abcd", name);
    }
}
