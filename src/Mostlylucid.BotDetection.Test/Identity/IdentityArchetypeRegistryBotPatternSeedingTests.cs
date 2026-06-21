using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Coverage for the BotPatterns -&gt; IdentityArchetypeRegistry promotion
///     path. Roots in this system are seed basins of attraction for
///     descendant emergence; without a Mastodon / GPTBot / Bingbot / etc.
///     root, observations of those clients can only drift toward whichever
///     hand-written YAML root happens to be nearest in centroid space, and
///     the descendants of (e.g.) a real Mastodon instance emerge under
///     chrome-xhr's umbrella -- exactly the failure mode the chrome-xhr.yaml
///     comment was originally written to warn about.
/// </summary>
public sealed class IdentityArchetypeRegistryBotPatternSeedingTests
{
    [Fact]
    public void Registry_loads_significantly_more_than_the_explicit_YAML_count()
    {
        var registry = NewRegistry();

        // Hand-written YAML archetypes live in
        // src/Mostlylucid.BotDetection/Definitions/IdentityArchetypes/*.yaml
        // and number ~16. Bot patterns add 141 known bot identities on top of
        // that. The exact total depends on YAML <-> pattern ID collisions
        // (explicit YAML wins, see below); the lower bound is the
        // synthesized-only count, the upper bound is YAML + Patterns with no
        // collisions. Either way >> 16.
        registry.All.Count.Should().BeGreaterThan(100,
            "every BotPatternEntry should be promoted to a root archetype so a Mastodon / GPTBot / " +
            "Bingbot fingerprint has its own basin to drift toward instead of being pulled into the " +
            "chrome-xhr or python-requests umbrella");

        // Sanity: every archetype_kind that the synthesizer can produce must
        // show up in the catalogue. A regression where the synthesizer drops
        // a category (e.g. only social-bot survives, ai-bot disappears) would
        // re-introduce the umbrella-misclassification bug for the missing
        // class without breaking the bulk root count.
        var kinds = registry.All.Select(a => a.ArchetypeKind).Distinct().ToList();
        kinds.Should().Contain(new[] { "verified-bot", "social-bot", "ai-bot", "tool", "scraper", "human-browser" },
            "the synthesized catalogue must populate every bot/tool/browser kind so each class has its own basin");
    }

    [Theory]
    // Mastodon: explicit YAML was deleted after a staging-data audit showed it
    // had no AssertedUaFamily + only 4 anti-discriminating dimensions, so it
    // ate every minimal-header request (curl, Chrome XHR, Bingbot, Edge). The
    // synthesized "Mastodon" root from the BotPatterns catalog stays --
    // FediverseDomainContributor + the composer's friendly-pin
    // (DeclaredBot && IsFriendlyBotType) handles real Mastodon clients.
    [InlineData("Mastodon", "social-bot")]     // synthesized from fediverse SocialMediaBot pattern
    // Pure-pattern paths (no explicit YAML, synthesizer wins):
    [InlineData("GPTBot", "ai-bot")]
    [InlineData("ClaudeBot", "ai-bot")]
    [InlineData("Bingbot", "verified-bot")]
    [InlineData("Pleroma", "social-bot")]      // fediverse SocialMediaBot pattern row
    // PerplexityBot: bot_type=GoodBot + ai_category=Search -> ai_category
    // overrides the base bot_type kind so the dashboard groups trusted AI
    // bots with their AI siblings, not with monitoring/search-engine bots.
    [InlineData("PerplexityBot", "ai-bot")]
    // Ahrefs is catalogued as bot_type=GoodBot, NOT Scraper, so it lands as verified-bot.
    [InlineData("AhrefsBot", "verified-bot")]
    public void Specific_bots_are_present_as_roots_with_correct_kind(string botName, string expectedKind)
    {
        var registry = NewRegistry();

        var match = registry.All.FirstOrDefault(a =>
            string.Equals(a.Name, botName, StringComparison.OrdinalIgnoreCase));

        match.Should().NotBeNull(
            $"{botName} is in the BotPatterns catalogue and must surface as a root archetype, " +
            "otherwise its observed fingerprints can only drift toward an unrelated umbrella root");
        match!.ArchetypeKind.Should().Be(expectedKind,
            "archetype_kind must mirror the hand-written IdentityArchetypes/*.yaml conventions so " +
            "dashboard groupings stay consistent across explicit + synthesized roots");
        match.ArchetypeRole.Should().Be("client");
    }

    [Fact]
    public void Explicit_YAML_archetype_wins_on_id_collision_with_synthesized_one()
    {
        // googlebot.yaml hand-writes the Googlebot root with Sec-Fetch /
        // Accept / Accept-Encoding pins -- a richer assertion than the
        // synthesizer's single hdr.ua_family pin. If a Googlebot pattern row
        // collides on archetype_id, the YAML version must survive intact.
        var registry = NewRegistry();
        var googlebotMatches = registry.All
            .Where(a => string.Equals(a.ArchetypeId, "googlebot", StringComparison.OrdinalIgnoreCase))
            .ToList();

        googlebotMatches.Should().ContainSingle("ID collisions must be resolved, not duplicated");

        // The explicit YAML carries a verified-bot archetype_kind. The
        // pattern catalogue lists Googlebot as bot_type=GoodBot which the
        // synthesizer also maps to verified-bot, so both paths land on the
        // same kind here -- but the YAML's description is the giveaway that
        // the explicit version was kept.
        googlebotMatches[0].Description.Should().NotContain("Auto-promoted",
            "explicit YAML archetype's description must NOT be replaced by the synthesized stub");
    }

    [Fact]
    public void KebabCase_handles_mixed_input()
    {
        IdentityArchetypeRegistry.KebabCase("GPTBot").Should().Be("gptbot");
        IdentityArchetypeRegistry.KebabCase("Ahrefs Bot").Should().Be("ahrefs-bot");
        IdentityArchetypeRegistry.KebabCase("New Relic").Should().Be("new-relic");
        IdentityArchetypeRegistry.KebabCase("Mastodon").Should().Be("mastodon");
        IdentityArchetypeRegistry.KebabCase("  ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("SearchEngine",   "verified-bot")]
    [InlineData("VerifiedBot",    "verified-bot")]
    [InlineData("MonitoringBot",  "verified-bot")]
    [InlineData("GoodBot",        "verified-bot")]
    [InlineData("AiBot",          "ai-bot")]
    [InlineData("SocialMediaBot", "social-bot")]
    [InlineData("Scraper",        "scraper")]
    [InlineData("Tool",           "tool")]
    [InlineData("MaliciousBot",   "malicious-bot")]
    [InlineData("ClickFraud",     "malicious-bot")]
    [InlineData("ExploitScanner", "malicious-bot")]
    [InlineData("Unknown",        "bot")]
    [InlineData(null,             "bot")]
    public void BotType_to_archetype_kind_mapping_covers_the_enum(string? botType, string expectedKind)
    {
        IdentityArchetypeRegistry.MapBotTypeToArchetypeKind(botType).Should().Be(expectedKind);
    }

    [Fact]
    public void Synthesizer_returns_null_for_a_blank_bot_name()
    {
        var entry = new BotPatternEntry { Pattern = "x", BotName = "", BotType = "Unknown" };
        IdentityArchetypeRegistry.SynthesizeFromBotPattern(entry).Should().BeNull();
    }

    [Fact]
    public void Synthesizer_emits_ua_family_pin_at_high_confidence()
    {
        var entry = new BotPatternEntry
        {
            Pattern = "Mastodon",
            BotName = "Mastodon",
            BotType = "SocialMediaBot",
            Vendor = "Mastodon",
        };
        var dto = IdentityArchetypeRegistry.SynthesizeFromBotPattern(entry);

        dto.Should().NotBeNull();
        dto!.ArchetypeId.Should().Be("mastodon");
        dto.Name.Should().Be("Mastodon");
        dto.ArchetypeKind.Should().Be("social-bot");
        dto.ArchetypeRole.Should().Be("client");
        dto.Dimensions.Should().ContainKey("hdr.ua_family",
            "the synthesized root must pin against the UA family so the matcher actually scores it; " +
            "without the assertion the centroid sits at all-zero and never competes for a match");
        dto.Dimensions!["hdr.ua_family"].Value.Should().Be("Mastodon");
        dto.Dimensions["hdr.ua_family"].Confidence.Should().BeGreaterThan(0.9,
            "the UA family pin must dominate the scoring contribution -- behaviour overrides it " +
            "per-fingerprint via descendant emergence, but the initial classification still needs " +
            "to land in the right basin");
    }

    [Fact]
    public void Adblocker_roots_are_loaded_with_human_adblocker_kind()
    {
        // Adblockers are a first-class human-positive signal per the system's
        // identity architecture. Without dedicated roots, a Chrome + uBlock
        // Origin visitor drifts into headless-chrome / scraper neighbourhoods
        // because the adblocker strips client hints + third-party requests.
        var registry = NewRegistry();

        var adblockerKinds = registry.All
            .Where(a => a.ArchetypeKind == "human-adblocker")
            .Select(a => a.ArchetypeId)
            .ToList();

        adblockerKinds.Should().Contain("ublock-origin");
        adblockerKinds.Should().Contain("adguard");
        adblockerKinds.Should().Contain("generic-adblocker",
            "the catch-all adblocker root must exist so visitors that exhibit " +
            "adblocker behaviour but don't fingerprint to a specific product still " +
            "drift into the human-adblocker basin");
    }

    [Fact]
    public void IngestWellKnownBots_adds_new_entries_and_preserves_existing()
    {
        var registry = NewRegistry();
        var initialCount = registry.All.Count;

        var added = registry.IngestWellKnownBots(new[]
        {
            ("fictitious-crawler",       "Fictitious Crawler",  "Scraper"),
            ("totally-new-monitor",      "Totally New Monitor", "MonitoringBot"),
            // Collides with explicit YAML / BotPatterns -- must be skipped, not duplicated.
            ("chrome-desktop",           "Chrome Desktop",      "Unknown"),
        });

        added.Should().Be(2, "two of the three triples are new; chrome-desktop collides with the explicit YAML root");
        registry.All.Count.Should().Be(initialCount + 2);
        registry.TryGetById("fictitious-crawler").Should().NotBeNull()
            .And.Subject.As<IdentityArchetype>().ArchetypeKind.Should().Be("scraper");
        registry.TryGetById("totally-new-monitor").Should().NotBeNull()
            .And.Subject.As<IdentityArchetype>().ArchetypeKind.Should().Be("verified-bot");
        // Re-ingesting the same set must be idempotent.
        registry.IngestWellKnownBots(new[]
        {
            ("fictitious-crawler", "Fictitious Crawler", "Scraper"),
        }).Should().Be(0);
    }

    [Fact]
    public void Synthesizer_routes_ai_category_to_ai_bot_regardless_of_bot_type()
    {
        var entry = new BotPatternEntry
        {
            Pattern = "PerplexityBot",
            BotName = "PerplexityBot",
            BotType = "GoodBot",
            AiCategory = "Search",
            Vendor = "Perplexity",
        };
        IdentityArchetypeRegistry.SynthesizeFromBotPattern(entry)!.ArchetypeKind.Should().Be("ai-bot",
            "ai_category is an orthogonal signal that operators care about for routing / rate-limit, " +
            "so a trusted AI bot must surface under ai-bot, not verified-bot");
    }

    private static IdentityArchetypeRegistry NewRegistry() =>
        new(NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1()));

    /// <summary>
    ///     Sparsity penalty regression guard. A staging-data audit found
    ///     mastodon.yaml (4 generic dims, no AssertedUaFamily) eating every
    ///     minimal-header request -- curl, Chrome XHR, Bingbot, Edge -- because
    ///     the masked-cosine score is `weightedLogLikelihood / totalMask`, which
    ///     AVERAGES over populated dims and rewards sparse archetypes whose few
    ///     dims happen to match. MaskedSimilarityCore now penalises archetypes
    ///     that populate fewer than MinPopulatedSlotsForMatch slots so a future
    ///     thin YAML can't re-trigger the same over-match regression.
    /// </summary>
    [Fact]
    public void Chrome_centroid_against_a_chrome_observation_returns_chrome_desktop()
    {
        // Regression guard for the staging-data audit that found mastodon.yaml
        // (4 generic dims, no AssertedUaFamily) eating every minimal-header
        // request because the per-dim averaging in MaskedSimilarityCore
        // structurally rewarded sparse archetypes. With mastodon.yaml deleted
        // and the sparsity penalty added, Chrome's own centroid against a
        // Chrome observation MUST land on chrome-desktop, not a synthesised
        // sparse competitor.
        var registry = NewRegistry();
        var chromeDesktop = registry.All.FirstOrDefault(a => a.Name == "Chrome Desktop");
        chromeDesktop.Should().NotBeNull("chrome-desktop.yaml is the canonical browser archetype");

        var nearest = registry.FindNearest(chromeDesktop!.Centroid, observedUaFamily: "Chrome");
        nearest.Should().NotBeNull();
        nearest!.Archetype.Name.Should().Be("Chrome Desktop",
            "Chrome's own centroid must score highest against chrome-desktop, not any sparser sibling");
    }
}