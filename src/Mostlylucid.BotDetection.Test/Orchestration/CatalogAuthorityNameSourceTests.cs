using System;
using System.Collections.Generic;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Characterisation suite for Step 4 of the single-source surgery.
///     Pin the EXISTING behaviour first, then the TARGET behaviour for the
///     refactor. The refactor: switch the catalog-authority BotType lookup
///     and the display-name fallback in DetectionLedgerExtensions to read
///     from <see cref="SignalKeys.UserAgentBotName"/> (the canonical signal
///     UserAgentContributor writes) rather than the contribution-aggregated
///     <c>ledger.BotName</c>. Once that source switch is in, the per-
///     contributor <c>botName: ...</c> writes become dead-code and can be
///     deleted in a follow-up commit.
///
///     These tests verify the assumption: <see cref="SignalKeys.UserAgentBotName"/>
///     IS sufficient to drive the catalog-authority chain without losing the
///     GoogleOther -> AiBot resolution recently landed in
///     <c>fix(catalog): GoogleOther is AiBot, not SearchEngine</c>.
/// </summary>
public class CatalogAuthorityNameSourceTests
{
    /// <summary>
    ///     Heuristic guess in the signal dict ("Scraper") gets overridden by
    ///     the catalog's authoritative type via the UserAgentBotName signal.
    ///     This is the exact regression the Step 4 source-switch protects
    ///     against: HeuristicEarly's bottom-of-the-barrel Scraper guess no
    ///     longer wins over the catalog's UA-pattern match.
    /// </summary>
    [Fact]
    public void Signal_UserAgentBotName_overrides_heuristic_botType_via_catalog()
    {
        var ledger = new DetectionLedger("test");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.7,
            Weight = 1.0,
            Reason = "Known bot pattern: GPTBot"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentIsBot] = true,
            [SignalKeys.UserAgentBotName] = "GPTBot",
            // HeuristicEarly's generic guess -- catalog must win.
            [SignalKeys.UserAgentBotType] = "Scraper",
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(BotType.AiBot, result.PrimaryBotType);
    }

    /// <summary>
    ///     TARGET shape after the refactor. Signal dict alone must carry
    ///     enough information for the catalog-authority chain to resolve
    ///     PrimaryBotType. Until the refactor lands this test asserts the
    ///     current (broken) behaviour and acts as the change marker.
    /// </summary>
    [Fact]
    public void Target_after_refactor_UserAgentBotName_signal_drives_catalog_authority()
    {
        var ledger = new DetectionLedger("test");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.7,
            Weight = 1.0,
            Reason = "Known bot pattern: GoogleOther"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentIsBot] = true,
            [SignalKeys.UserAgentBotName] = "GoogleOther",
            // HeuristicEarly's generic guess - the catalog must override this.
            [SignalKeys.UserAgentBotType] = "Scraper",
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        // After the refactor: catalog-authority reads the signal, finds
        // GoogleOther in the YAML, classifies as AiBot. Heuristic Scraper
        // guess is overridden.
        Assert.Equal(BotType.AiBot, result.PrimaryBotType);
        Assert.Equal("GoogleOther", result.PrimaryBotName);
    }

    /// <summary>
    ///     SAFETY: a request with NO bot signals must not yield a false
    ///     catalog match. PrimaryBotName / PrimaryBotType stay null so the
    ///     orchestrator's policy gate doesn't apply bot-treatment to humans.
    /// </summary>
    [Fact]
    public void No_bot_signals_at_all_yields_null_PrimaryBotType()
    {
        var ledger = new DetectionLedger("test");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "Header",
            Category = "Header",
            ConfidenceDelta = 0.0,
            Weight = 1.0,
            Reason = "Clean browser headers"
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgentIsBot] = false,
        };

        var result = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Null(result.PrimaryBotType);
    }

    /// <summary>
    ///     REGRESSION (staging f69ff9c7…: Semrush shown as "unknown"). In production the
    ///     UA atom raises its catalog identity via <c>sink.Raise("ua.bot_name:…")</c> and
    ///     never populates <c>contribution.Signals</c>, so <c>preSignals</c> (built from
    ///     <c>ledger.MergedSignals</c>) does NOT carry <see cref="SignalKeys.UserAgentBotName"/>.
    ///     The name resolvers used to read preSignals directly, so every catalog bot named
    ///     only by the UA atom (SemrushBot, SEO tools, AI scrapers) resolved to "Unknown".
    ///     The fix makes the name/type reads sink-first (like the pre-existing ReadBool).
    ///     This test pins the production shape: signal in the SINK, absent from preSignals.
    /// </summary>
    [Fact]
    public void Sink_only_UserAgentBotName_drives_display_name_when_preSignals_lacks_it()
    {
        var ledger = new DetectionLedger("test");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.7,
            Weight = 1.0,
            Reason = "Known bot pattern: GPTBot"
        });

        // The atom's real emit path: raised on the sink, NOT in the merged-signal dict.
        var sink = new SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.UserAgentIsBot}:true", "test");
        sink.Raise($"{SignalKeys.UserAgentBotName}:GPTBot", "test");
        sink.Raise($"{SignalKeys.UserAgentBotType}:Scraper", "test"); // heuristic guess; catalog must win

        // preSignals shape in production: EMPTY of the sink-only UA signals.
        var result = ledger.ToAggregatedEvidence(
            aiRan: false, premergedSignals: new Dictionary<string, object>(), sink: sink);

        // Before the fix this was "Unknown". Catalog identity now reaches the resolver.
        Assert.Equal("GPTBot", result.PrimaryBotName);
        Assert.Equal(BotType.AiBot, result.PrimaryBotType); // catalog authority via sink
    }
}