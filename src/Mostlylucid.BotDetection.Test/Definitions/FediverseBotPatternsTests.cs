using Mostlylucid.BotDetection.Definitions.BotPatterns;

namespace Mostlylucid.BotDetection.Test.Definitions;

/// <summary>
///     Locks in the fediverse YAML catalog entries Bug K added. Each missing
///     entry would re-introduce the failure mode the live Iceshrimp signature
///     (dE6rEAecvb3KKRsfUGui5w on www.stylobot.net) demonstrated: the UA
///     matcher detects the name, but FindBotTypeByName returns null, so the
///     friendly-pin gate exits "not-applicable:yamlType=null" and the
///     reputation cache then latches ConfirmedBad on the first visit.
/// </summary>
public class FediverseBotPatternsTests
{
    [Theory]
    // The set the live repro triggered against.
    [InlineData("Iceshrimp")]
    // Modern Mastodon/Misskey forks that have similar UA shape and were also
    // missing from the catalog before this commit.
    [InlineData("Sharkey")]
    [InlineData("Firefish")]
    [InlineData("GoToSocial")]
    [InlineData("Mitra")]
    [InlineData("Bonfire")]
    [InlineData("Mbin")]
    [InlineData("Snac")]
    [InlineData("Takahe")]
    // Pre-existing entries that the new ones live alongside -- guard against
    // an accidental YAML break removing them.
    [InlineData("Mastodon")]
    [InlineData("Pleroma")]
    [InlineData("Misskey")]
    [InlineData("Akkoma")]
    public void FindBotTypeByName_ReturnsSocialMediaBot(string botName)
    {
        var botType = BotPatternLoader.Default.FindBotTypeByName(botName);

        Assert.Equal("SocialMediaBot", botType);
    }

    [Fact]
    public void MatchUserAgent_RealIceshrimpUa_TagsAsSocialMediaBot()
    {
        // Sampled shape of an Iceshrimp instance's outbound UA when fetching
        // ActivityPub objects: vendor + version + the +https://host/ self-id.
        var ua = "Iceshrimp/2024.7.0 (+https://example-iceshrimp.example.net)";

        var (botType, botName) = BotPatternLoader.Default.MatchUserAgent(ua);

        Assert.Equal("SocialMediaBot", botType);
        Assert.Equal("Iceshrimp", botName);
    }
}