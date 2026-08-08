using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Locks the 8.8 fix for "seed-rule predicates reference facets nothing produces".
///
///     <para>
///         <b>The defect.</b> Six of the eight shipped seed rules -- including both
///         defaults -- named facets (<c>bot.type</c>, <c>is_human</c>,
///         <c>score.bot_probability</c>) that no layer produced.
///         <c>PredicateEvaluator.EvaluateTerm</c> returns <c>false</c> on an unknown
///         facet, so a dead facet is indistinguishable from a legitimately-false
///         predicate: the rules never fired and never complained. The propagation
///         vector was the doc comment on <c>Predicate.Term</c>, which offered two
///         non-existent keys as its canonical examples.
///     </para>
///
///     <para>
///         <b>The fix.</b> <see cref="VerdictFacetProjection"/> projects
///         <c>score.bot_probability</c> and <c>is_human</c> at every evidence-building
///         site that produces a real verdict. This suite proves the shipped default
///         block rule now fires for a confirmed bot and still does not fire for a
///         human -- the original runtime proof, inverted into a regression test.
///     </para>
///
///     <para>
///         <b>Every assertion is paired with a control.</b> "The rule fired" is worth
///         little without "and it does not fire for everyone"; "the rule did not fire"
///         is vacuous if the harness never reached the rule. Both directions are
///         asserted against the same corpus, scope and resolver.
///     </para>
/// </summary>
public class SeedRuleFacetReachabilityTests
{
    /// <summary>
    ///     <c>wildcard-default-block-confirmed-bot.yaml</c> -- one of the two shipped
    ///     defaults. Predicate: <c>score.bot_probability &gt;= 0.9</c>.
    /// </summary>
    private static readonly Guid WildcardBlockConfirmedBot =
        Guid.Parse("00000000-0000-0000-0000-000000000002");

    private const string SeedResourcePrefix = "Mostlylucid.BotDetection.Policies.Rules.SeedRules.";

    /// <summary>The real shipped seed corpus, loaded from embedded resources exactly as production does.</summary>
    private static async Task<DefaultPolicyResolver> RealSeedResolverAsync()
    {
        var store = YamlPolicyRuleStore.FromEmbeddedResources(
            typeof(YamlPolicyRuleStore).Assembly,
            SeedResourcePrefix);
        await store.InitializeAsync();
        return new DefaultPolicyResolver(store, contributors: null);
    }

    /// <summary>
    ///     Evidence built through the PRODUCTION path: contributions on a real
    ///     <see cref="DetectionLedger"/> plus sink-raised hints, run through
    ///     <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/>. Deliberately NOT
    ///     <c>premergedSignals</c> -- that bypasses the sink projection under test.
    /// </summary>
    /// <param name="asBot">
    ///     true for a confirmed scanner; false for ordinary human browsing.
    /// </param>
    private static AggregatedEvidence BuildEvidence(bool asBot)
    {
        var ledger = new DetectionLedger($"seed-facet-{(asBot ? "bot" : "human")}");
        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        const string session = "seed-facet-session";

        if (asBot)
        {
            ledger.AddContribution(DetectionContribution.Bot(
                "SecurityTool",
                "SecurityTool",
                confidence: 0.95,
                reason: "Confirmed scanner user-agent",
                weight: 3.0,
                botType: BotType.Scraper.ToString()));
            sink.Raise($"{SignalKeys.UserAgentBotType}:{BotType.Scraper}", session);
            sink.Raise($"{SignalKeys.UserAgentBotName}:TestScanner", session);
        }
        else
        {
            ledger.AddContribution(new DetectionContribution
            {
                DetectorName = "Header",
                Category = "Header",
                ConfidenceDelta = -0.6,
                Weight = 2.0,
                Reason = "Full browser header set",
            });
        }

        // aiRan: true so the NonAiMaxProbability 0.90 ceiling doesn't clamp the bot
        // score onto the rule's >= 0.9 boundary, where the assertion would turn on a
        // floating-point tie rather than on the facet.
        return ledger.ToAggregatedEvidence(aiRan: true, sink: sink);
    }

    private static Dictionary<string, object?> AsRequestSignals(AggregatedEvidence evidence) =>
        evidence.Signals.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    /// <summary>
    ///     THE REGRESSION LOCK. A confirmed bot now matches the shipped default block
    ///     rule. Before the projection this could not fire for anyone.
    /// </summary>
    [Fact]
    public async Task Confirmed_bot_matches_the_shipped_default_block_rule()
    {
        var evidence = BuildEvidence(asBot: true);

        // Precondition: the input really IS a confirmed bot, so a match below is
        // attributable to the verdict rather than to a mis-built fixture.
        Assert.True(
            evidence.BotProbability >= 0.9,
            $"precondition failed: synthetic input is not a confirmed bot (probability {evidence.BotProbability:F3})");

        var resolver = await RealSeedResolverAsync();
        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            AsRequestSignals(evidence));

        Assert.Contains(matched, r => r.Rule.Id == WildcardBlockConfirmedBot);
    }

    /// <summary>
    ///     THE CONTROL -- must NOT match. Same corpus, scope and resolver; only the
    ///     verdict differs. Guards against the projection being written in a way that
    ///     makes the rule fire for everything, which would read as "fixed" while
    ///     blocking humans.
    /// </summary>
    [Fact]
    public async Task Human_browsing_does_not_match_the_shipped_default_block_rule()
    {
        var evidence = BuildEvidence(asBot: false);

        Assert.True(
            evidence.BotProbability < 0.9,
            $"precondition failed: synthetic human scored as a confirmed bot ({evidence.BotProbability:F3})");

        var resolver = await RealSeedResolverAsync();
        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            AsRequestSignals(evidence));

        Assert.DoesNotContain(matched, r => r.Rule.Id == WildcardBlockConfirmedBot);
    }

    /// <summary>
    ///     The projected facets exist, carry the right values, and are derived from the
    ///     verdict rather than invented.
    /// </summary>
    [Fact]
    public void Verdict_facets_are_projected_into_a_real_detections_signals()
    {
        var bot = BuildEvidence(asBot: true);
        var human = BuildEvidence(asBot: false);

        Assert.Contains(SignalKeys.ScoreBotProbability, bot.Signals.Keys);
        Assert.Contains(SignalKeys.IsHuman, bot.Signals.Keys);

        Assert.Equal(bot.BotProbability, Convert.ToDouble(bot.Signals[SignalKeys.ScoreBotProbability]));
        Assert.Equal(false, bot.Signals[SignalKeys.IsHuman]);
        Assert.Equal(true, human.Signals[SignalKeys.IsHuman]);

        // The real bot-type facet is produced and correctly split from its raised
        // "key:value" hint. `bot.type` remains absent by design -- the seed rules are
        // being renamed onto ua.bot_type, not the reverse.
        Assert.Contains(SignalKeys.UserAgentBotType, bot.Signals.Keys);
        Assert.Equal(BotType.Scraper.ToString(), bot.Signals[SignalKeys.UserAgentBotType]?.ToString());
        Assert.DoesNotContain("bot.type", bot.Signals.Keys);
    }

    /// <summary>
    ///     "BOTS ARE NEVER HUMAN" (operator, 2026-06-20). A catalogue-identified bot
    ///     sitting BELOW the probability floor must still project
    ///     <c>is_human = false</c>. A probability-only derivation would report it human
    ///     and reintroduce the Bytespider contradiction
    ///     (<c>CatalogIdentityIsBotTests</c>): dashboard says Human, BotName says
    ///     Bytespider. This is the trap the projection was written to avoid.
    /// </summary>
    [Fact]
    public void Catalog_identified_bot_below_the_floor_is_not_projected_as_human()
    {
        var ledger = new DetectionLedger("seed-facet-catalog-bot");
        // Deliberately weak: not enough on its own to clear Classification.BotFloor.
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = -0.2,
            Weight = 0.5,
            Reason = "Header pattern marginal",
        });

        var evidence = ledger.ToAggregatedEvidence(
            aiRan: false,
            premergedSignals: new Dictionary<string, object>
            {
                [SignalKeys.UserAgent] = "Mozilla/5.0 (compatible; Bytespider; spider-feedback@bytedance.com)",
                [SignalKeys.UserAgentBotName] = "Bytespider",
            });

        // Precondition: this is the below-the-floor case the rule is about.
        Assert.True(
            evidence.BotProbability < 0.70,
            $"precondition failed: fixture cleared the bot floor ({evidence.BotProbability:F3}), so it no longer tests identity-over-probability");
        Assert.NotNull(evidence.PrimaryBotType);

        Assert.Equal(false, evidence.Signals[SignalKeys.IsHuman]);
    }
}
