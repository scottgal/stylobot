using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Definitions;

/// <summary>
///     Pins the GoogleOther classification override. Google's own docs
///     (developers.google.com/search/docs/crawling-indexing/google-common-crawlers)
///     state GoogleOther is the "generic crawler" used by various product
///     teams for R&amp;D fetches, NOT a Search-Engine crawler, and that
///     blocking it has no effect on Search ranking. Arcjet's well-known-bots
///     catalog tags it under the "google" category which our
///     <see cref="Mostlylucid.BotDetection.Definitions.WellKnownBots.WellKnownBotIndex"/>
///     lumps with SearchEngine -- too coarse for an operator's "allow search,
///     block AI scrapers" policy. The YAML override classifies it as AiBot,
///     matching its observed in-the-wild use as a research / training
///     crawler. Adding it to the YAML means BotPatternLoader's substring
///     match wins before Arcjet's category mapping gets a vote.
/// </summary>
public class GoogleOtherClassificationTests
{
    [Theory]
    [InlineData("GoogleOther")]
    [InlineData("GoogleOther-Image")]
    [InlineData("GoogleOther-Video")]
    public void FindBotTypeByName_ReturnsAiBot(string botName)
    {
        var type = BotPatternLoader.Default.FindBotTypeByName(botName);
        Assert.Equal(BotType.AiBot.ToString(), type);
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/[phone] Mobile Safari/537.36 (compatible; GoogleOther)",
        "GoogleOther")]
    [InlineData(
        "Mozilla/5.0 (compatible; GoogleOther-Image/1.0; +http://www.google.com/bot.html)",
        "GoogleOther-Image")]
    [InlineData(
        "Mozilla/5.0 (compatible; GoogleOther-Video/1.0; +http://www.google.com/bot.html)",
        "GoogleOther-Video")]
    public void MatchUserAgent_ReturnsAiBot_ForGoogleOtherUas(string userAgent, string expectedName)
    {
        var (botType, botName) = BotPatternLoader.Default.MatchUserAgent(userAgent);
        Assert.Equal(BotType.AiBot.ToString(), botType);
        Assert.Equal(expectedName, botName);
    }

    [Fact]
    public void Specific_variants_must_match_before_the_bare_GoogleOther_pattern()
    {
        // Pin the ordering invariant. BotPatternLoader.MatchUserAgent does
        // substring matches in declared YAML order. If GoogleOther-Image's
        // entry slips below the bare GoogleOther entry, both UAs would
        // resolve to "GoogleOther" -- losing the image/video variant
        // distinction the operator needs for per-product policy.
        var (_, imageName) = BotPatternLoader.Default.MatchUserAgent(
            "Mozilla/5.0 (compatible; GoogleOther-Image/1.0)");
        var (_, videoName) = BotPatternLoader.Default.MatchUserAgent(
            "Mozilla/5.0 (compatible; GoogleOther-Video/1.0)");
        Assert.Equal("GoogleOther-Image", imageName);
        Assert.Equal("GoogleOther-Video", videoName);
    }
}