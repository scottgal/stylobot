using FluentAssertions;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Definitions;

/// <summary>
///     Unit tests for <see cref="WellKnownBotIndex"/>.
///     Covers UA matching, forbidden-pattern exclusion, display name
///     formatting, category-to-BotType mapping, and atomic replacement.
/// </summary>
public sealed class WellKnownBotIndexTests
{
    private static WellKnownBotEntry MakeEntry(
        string id,
        string[] categories,
        string[] accepted,
        string[]? forbidden = null) => new()
    {
        Id = id,
        Categories = categories,
        Pattern = new WellKnownBotPattern
        {
            Accepted = accepted,
            Forbidden = forbidden ?? []
        }
    };

    // ── Empty-index behaviour ────────────────────────────────────────────────

    [Fact]
    public void Empty_index_returns_null_for_any_ua()
    {
        var sut = new WellKnownBotIndex();
        sut.TryMatch("Googlebot/2.1").Should().BeNull();
    }

    [Fact]
    public void Empty_index_has_count_zero()
    {
        var sut = new WellKnownBotIndex();
        sut.Count.Should().Be(0);
    }

    // ── Basic matching ───────────────────────────────────────────────────────

    [Fact]
    public void TryMatch_returns_null_when_no_pattern_matches()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("bingbot", ["search-engine"], ["bingbot\\/"])]);

        sut.TryMatch("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120").Should().BeNull();
    }

    [Fact]
    public void TryMatch_returns_match_for_googlebot_ua()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("google-crawler", ["google", "search-engine"], ["Googlebot\\/"])]);

        var match = sut.TryMatch("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)");

        match.Should().NotBeNull();
        match!.Id.Should().Be("google-crawler");
        match.BotType.Should().Be(BotType.SearchEngine);
        match.DisplayName.Should().Be("Google Crawler");
        // "Googlebot\/" contains a metachar so it compiles as a REGEX — no literal
        // family is recoverable, and Family falls back to DisplayName (never empty).
        match.Family.Should().Be("Google Crawler");
    }

    [Fact]
    public void TryMatch_family_is_the_accepted_literal_not_the_title_cased_id()
    {
        // Regression 2026-08-12 (operator P0): the evidence-level bot name was the
        // title-cased catalog id ("Petalsearch Crawler") instead of the bot's own
        // family ("PetalBot"), surfacing raw crawler text on Top Bots. The Family
        // must be the accepted literal the UA actually carried.
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("petalsearch-crawler", ["ai", "search-engine"], ["PetalBot"])]);

        var match = sut.TryMatch("Mozilla/5.0 (compatible; PetalBot; +https://webmaster.petalsearch.com/)");

        match.Should().NotBeNull();
        match!.Id.Should().Be("petalsearch-crawler");
        match.DisplayName.Should().Be("Petalsearch Crawler");
        match.Family.Should().Be("PetalBot");
    }

    [Fact]
    public void TryMatch_is_case_insensitive()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("bingbot", ["search-engine"], ["bingbot"])]);

        sut.TryMatch("Mozilla/5.0 (compatible; BINGBOT/2.0)").Should().NotBeNull();
        sut.TryMatch("Mozilla/5.0 (compatible; bingbot/2.0)").Should().NotBeNull();
    }

    [Fact]
    public void TryMatch_returns_null_for_empty_ua()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("bot", ["tool"], ["bot"])]);

        sut.TryMatch(null).Should().BeNull();
        sut.TryMatch("").Should().BeNull();
    }

    // ── Forbidden patterns ───────────────────────────────────────────────────

    [Fact]
    public void TryMatch_returns_null_when_forbidden_pattern_matches()
    {
        // The arcjet schema allows entries with forbidden patterns that negate
        // otherwise-matching accepted patterns.
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("test-bot",
            ["tool"],
            accepted: ["TestBot"],
            forbidden: ["FakeTestBot"])]);

        sut.TryMatch("FakeTestBot/1.0").Should().BeNull();
    }

    [Fact]
    public void TryMatch_returns_match_when_accepted_matches_and_forbidden_does_not()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("test-bot",
            ["tool"],
            accepted: ["TestBot"],
            forbidden: ["FakeTestBot"])]);

        sut.TryMatch("TestBot/1.0 (real)").Should().NotBeNull();
    }

    // ── Display name formatting ──────────────────────────────────────────────

    [Theory]
    [InlineData("google-crawler", "Google Crawler")]
    [InlineData("bingbot", "Bingbot")]
    [InlineData("gpt-bot", "Gpt Bot")]
    [InlineData("amazon-alexa", "Amazon Alexa")]
    [InlineData("ia-archiver", "Ia Archiver")]
    public void DisplayName_converts_kebab_case_to_title_case(string id, string expectedName)
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry(id, ["tool"], ["placeholder"])]);

        // Load a UA that matches the placeholder pattern
        var match = sut.TryMatch("placeholder");
        match!.DisplayName.Should().Be(expectedName);
    }

    // ── BotType category mapping ─────────────────────────────────────────────

    [Theory]
    [InlineData("search-engine")]
    [InlineData("google")]
    [InlineData("microsoft")]
    [InlineData("meta")]
    [InlineData("amazon")]
    [InlineData("apple")]
    [InlineData("yahoo")]
    public void SearchEngine_categories_map_to_SearchEngine_BotType(string category)
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("x", [category], ["x-bot"])]);

        sut.TryMatch("x-bot")!.BotType.Should().Be(BotType.SearchEngine);
    }

    [Fact]
    public void Ai_category_maps_to_AiBot()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("gpt-bot", ["ai"], ["GPTBot"])]);

        sut.TryMatch("GPTBot/1.0")!.BotType.Should().Be(BotType.AiBot);
    }

    [Fact]
    public void Social_category_maps_to_SocialMediaBot()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("facebot", ["social"], ["facebookexternalhit"])]);

        sut.TryMatch("facebookexternalhit/1.1")!.BotType.Should().Be(BotType.SocialMediaBot);
    }

    [Fact]
    public void Monitor_category_maps_to_MonitoringBot()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("uptimerobot", ["monitor"], ["UptimeRobot"])]);

        sut.TryMatch("UptimeRobot/2.0")!.BotType.Should().Be(BotType.MonitoringBot);
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("programmatic")]
    [InlineData("vercel")]
    public void Tool_categories_map_to_Tool_BotType(string category)
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("x", [category], ["x-tool"])]);

        sut.TryMatch("x-tool")!.BotType.Should().Be(BotType.Tool);
    }

    [Theory]
    [InlineData("academic")]
    [InlineData("archive")]
    [InlineData("feedfetcher")]
    [InlineData("preview")]
    [InlineData("advertising")]
    [InlineData("webhook")]
    [InlineData("slack")]
    public void GoodBot_categories_map_to_GoodBot(string category)
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("x", [category], ["x-good-bot"])]);

        sut.TryMatch("x-good-bot")!.BotType.Should().Be(BotType.GoodBot);
    }

    [Fact]
    public void Unknown_category_maps_to_Unknown()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("mystery-bot", ["unknown"], ["MysteryBot"])]);

        sut.TryMatch("MysteryBot/1.0")!.BotType.Should().Be(BotType.Unknown);
    }

    [Fact]
    public void First_matching_category_wins_for_multi_category_entries()
    {
        // e.g. ["google", "search-engine"] - first recognized category takes priority
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("google-crawler", ["google", "search-engine"], ["Googlebot\\/"])]);

        // Both "google" and "search-engine" map to SearchEngine -- verify the entry is found
        sut.TryMatch("Googlebot/2.1")!.BotType.Should().Be(BotType.SearchEngine);
    }

    // ── Replace atomicity ────────────────────────────────────────────────────

    [Fact]
    public void Replace_updates_count()
    {
        var sut = new WellKnownBotIndex();

        sut.Replace([
            MakeEntry("bot-a", ["tool"], ["BotA"]),
            MakeEntry("bot-b", ["tool"], ["BotB"])
        ]);

        sut.Count.Should().Be(2);
    }

    [Fact]
    public void Replace_removes_old_entries()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("old-bot", ["tool"], ["OldBot"])]);
        sut.Replace([MakeEntry("new-bot", ["tool"], ["NewBot"])]);

        sut.TryMatch("OldBot/1.0").Should().BeNull();
        sut.TryMatch("NewBot/1.0").Should().NotBeNull();
    }

    [Fact]
    public void Replace_skips_entries_with_no_accepted_patterns()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([
            MakeEntry("empty", ["tool"], []),         // no patterns - skipped
            MakeEntry("real", ["tool"], ["RealBot"])
        ]);

        sut.Count.Should().Be(1);
    }

    [Fact]
    public void Replace_skips_entries_with_malformed_regex()
    {
        var sut = new WellKnownBotIndex();
        sut.Replace([
            MakeEntry("bad-regex", ["tool"], ["[invalid"]),  // invalid regex - skipped
            MakeEntry("valid", ["tool"], ["ValidBot"])
        ]);

        // The invalid-regex entry is skipped; only the valid one survives
        sut.Count.Should().Be(1);
        sut.TryMatch("ValidBot/1.0").Should().NotBeNull();
    }

    // ── Required-core pre-check (scan optimization) ──────────────────────────
    // The per-regex required-core pre-check skips a regex when its leading required
    // literal is absent from the UA. These lock in that it never changes a match:
    // a matching UA still matches, a non-matching UA still misses.

    [Fact]
    public void RequiredCore_present_regex_still_matches()
    {
        // Core "DataMinerBot" (before the '\/' metachar) IS present -> regex runs -> match.
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("dataminer", ["tool"], ["DataMinerBot\\/[0-9]"])]);

        sut.TryMatch("DataMinerBot/3.1 (crawler)").Should().NotBeNull();
    }

    [Fact]
    public void RequiredCore_absent_still_misses()
    {
        // Core "DataMinerBot" absent -> pre-check skips the regex -> no match (unchanged).
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("dataminer", ["tool"], ["DataMinerBot\\/[0-9]"])]);

        sut.TryMatch("Mozilla/5.0 (compatible; SomethingElse/1.0)").Should().BeNull();
    }

    [Fact]
    public void RequiredCore_grouped_alternation_matches_all_branches()
    {
        // A grouped alternation "(foo|bar)crawler" opens with '(' -> null core -> the
        // regex always runs, so BOTH branches match (no false negative on either).
        var sut = new WellKnownBotIndex();
        sut.Replace([MakeEntry("multi", ["tool"], ["(alpha|bravo)crawler"])]);

        sut.TryMatch("alphacrawler/1.0").Should().NotBeNull();
        sut.TryMatch("bravocrawler/1.0").Should().NotBeNull();
    }
}