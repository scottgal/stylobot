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
///     RUNTIME PROOF for the 8.8 finding "seed-rule predicates reference facets
///     nothing produces". Static analysis said 6 of 8 shipped seed rules are inert
///     because their facet NAMES (<c>bot.type</c>, <c>is_human</c>,
///     <c>score.bot_probability</c>) are produced by nothing. This suite proves it
///     by driving a real confirmed-bot detection through the real seed corpus and
///     the real resolver.
///
///     <para>
///         Why the facet, not the value: <c>PredicateEvaluator.EvaluateTerm</c> does
///         an ordinal <c>TryGetValue</c> on the facet and returns <c>false</c> on a
///         miss ("silent miss -- unknown facet"). A dead facet is therefore
///         indistinguishable from a legitimately-false predicate, which is why this
///         has shipped green. A validator that only checked predicate VALUES against
///         their enums would pass all six of these rules.
///     </para>
///
///     <para>
///         <b>Every test here is paired with a control that MUST match.</b> A test
///         asserting only "the rule did not fire" is vacuous -- it passes just as
///         happily when the harness never reached the rule at all. The control
///         injects the missing facet and asserts the SAME rule DOES fire, which
///         proves the rule, the corpus, the scope and the resolver are all wired
///         and that the sole difference is the facet's presence.
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
    ///     A genuinely confirmed-bot detection, built through the PRODUCTION path:
    ///     contributions on a real <see cref="DetectionLedger"/> plus sink-raised
    ///     hints, projected into <c>evidence.Signals</c> by the real
    ///     <c>SinkEvidenceReader.ProjectSinkSignals</c> call inside
    ///     <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/>.
    ///     Deliberately NOT <c>premergedSignals</c> -- that bypasses the projection
    ///     under test.
    /// </summary>
    private static AggregatedEvidence ConfirmedBotEvidence()
    {
        var ledger = new DetectionLedger("seed-facet-reachability");
        ledger.AddContribution(DetectionContribution.Bot(
            "SecurityTool",
            "SecurityTool",
            confidence: 0.95,
            reason: "Confirmed scanner user-agent",
            weight: 3.0,
            botType: BotType.Scraper.ToString()));

        var sink = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        const string session = "seed-facet-session";
        sink.Raise($"{SignalKeys.UserAgentBotType}:{BotType.Scraper}", session);
        sink.Raise($"{SignalKeys.UserAgentBotName}:TestScanner", session);

        // aiRan: true so the NonAiMaxProbability 0.90 ceiling doesn't clamp the
        // score onto the rule's >= 0.9 boundary, where the assertion would turn on
        // a floating-point tie rather than on the facet.
        return ledger.ToAggregatedEvidence(aiRan: true, sink: sink);
    }

    private static Dictionary<string, object?> AsRequestSignals(AggregatedEvidence evidence) =>
        evidence.Signals.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    /// <summary>
    ///     THE PROOF. A confirmed bot (probability >= 0.9) does NOT match the shipped
    ///     default block rule, because <c>score.bot_probability</c> is not a key any
    ///     layer populates.
    /// </summary>
    [Fact]
    public async Task Confirmed_bot_does_not_match_the_shipped_default_block_rule()
    {
        var evidence = ConfirmedBotEvidence();

        // Precondition: the input really IS a confirmed bot. Without this the
        // "no match" assertion below would be trivially true for a human.
        Assert.True(
            evidence.BotProbability >= 0.9,
            $"precondition failed: synthetic input is not a confirmed bot (probability {evidence.BotProbability:F3})");

        var resolver = await RealSeedResolverAsync();
        var matched = await resolver.EffectiveWithContextAsync(
            PolicyScope.Wildcard(),
            AsRequestSignals(evidence));

        Assert.DoesNotContain(matched, r => r.Rule.Id == WildcardBlockConfirmedBot);
    }

    /// <summary>
    ///     THE CONTROL -- must match. Same corpus, same scope, same resolver, same
    ///     evidence; the ONLY difference is that <c>score.bot_probability</c> is
    ///     present. If this fails, the harness never reached the rule and the test
    ///     above proves nothing.
    /// </summary>
    [Fact]
    public async Task Control_same_request_matches_once_the_missing_facet_is_supplied()
    {
        var evidence = ConfirmedBotEvidence();
        var signals = AsRequestSignals(evidence);
        signals["score.bot_probability"] = 0.95m;

        var resolver = await RealSeedResolverAsync();
        var matched = await resolver.EffectiveWithContextAsync(PolicyScope.Wildcard(), signals);

        Assert.Contains(matched, r => r.Rule.Id == WildcardBlockConfirmedBot);
    }

    /// <summary>
    ///     Facet-level proof: the three names the seed rules reference are absent
    ///     from a real detection's signal bag, while the name they SHOULD use
    ///     (<c>ua.bot_type</c>) is present and correctly split from its raised value.
    /// </summary>
    [Fact]
    public void Seed_rule_facets_are_absent_from_a_real_detections_signals()
    {
        var evidence = ConfirmedBotEvidence();

        Assert.DoesNotContain("bot.type", evidence.Signals.Keys);
        Assert.DoesNotContain("is_human", evidence.Signals.Keys);
        Assert.DoesNotContain("score.bot_probability", evidence.Signals.Keys);

        // Control: the real facet IS produced, so absence above is a property of
        // those three names and not of the projection being empty.
        Assert.Contains(SignalKeys.UserAgentBotType, evidence.Signals.Keys);
        Assert.Equal(
            BotType.Scraper.ToString(),
            evidence.Signals[SignalKeys.UserAgentBotType]?.ToString());
    }
}
