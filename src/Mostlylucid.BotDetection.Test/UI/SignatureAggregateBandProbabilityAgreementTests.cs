using System;
using System.Collections.Generic;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     REPRODUCTION of the operator P0 (2026-08-08): a Top Bots row rendering
///     "Very Low Risk Profile: 98% bot probability".
///
///     <para>
///         <b>Mechanism.</b> <see cref="SignatureAggregateCache.WarmFromDetections"/>
///         folds ONE detection window into ONE <c>ResolvedVerdict</c> using TWO
///         DIFFERENT aggregation functions:
///         <list type="bullet">
///           <item><c>BotProbability</c> = <b>sticky MAX</b> across the window
///                 (<c>SignatureAggregateCache.cs:590</c>) whenever the signature
///                 carries a named (non-Unknown) BotType.</item>
///           <item><c>RiskBand</c> = <b>MAJORITY vote</b> across the window
///                 (<c>SignatureAggregateCache.cs:558</c>).</item>
///         </list>
///         Max and mode are unrelated statistics. A crawl whose requests are mostly
///         low-signal (assets, 304s) with a handful of identifying hits produces
///         <c>max = 0.98</c> and <c>mode = VeryLow</c> from the same rows, and
///         <c>_RiskBadge.cshtml</c> renders both in ONE sentence.
///     </para>
///
///     <para>
///         <b>This is not the RiskBand semantics question.</b> Nothing here depends
///         on what RiskBand MEANS. Even under the operator's activity-risk ruling,
///         a max-aggregated number paired with a mode-aggregated band still
///         disagrees. The semantics change and this defect are independent, and
///         fixing the semantics alone leaves this row exactly as wrong.
///     </para>
/// </summary>
public class SignatureAggregateBandProbabilityAgreementTests
{
    /// <summary>
    ///     The live shape: a catalogue-identified crawler whose window is dominated
    ///     by low-signal asset requests, with one identifying hit at 0.98.
    /// </summary>
    private static List<DashboardDetectionEvent> AssetHeavyCrawlWindow(
        double latestThreatScore = 0.0, string latestThreatBand = "None")
    {
        var now = DateTime.UtcNow;
        var rows = new List<DashboardDetectionEvent>
        {
            // detections[0] must be the freshest -- the method documents
            // "timestamp DESC ordering" and takes detections[0] as `latest`.
            Detection("sig-crawler", 0.98, "VeryHigh", now, latestThreatScore, latestThreatBand),
        };

        // ...followed by the low-signal bulk that wins the majority vote.
        for (var i = 1; i <= 9; i++)
            rows.Add(Detection("sig-crawler", 0.10, "VeryLow", now.AddSeconds(-i * 10), 0.0, "None"));

        return rows;
    }

    /// <summary>
    ///     THE INVARIANT, stated so it survives the pending RiskBand semantics change:
    ///     the warmed band must be what the seeded facts DERIVE to, not the mode of the
    ///     window. Asserting derivation-parity rather than "band must be high at high
    ///     probability" keeps this test honest under the activity-risk ruling, where a
    ///     corroborated benign crawler is legitimately low-risk at 98%.
    /// </summary>
    [Fact]
    public void Warmed_band_is_derived_from_the_seeded_facts_not_voted_across_the_window()
    {
        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());

        cache.WarmFromDetections("sig-crawler", AssetHeavyCrawlWindow());

        var verdict = cache.GetResolvedVerdict("sig-crawler");
        Assert.NotNull(verdict);

        // Precondition: sticky-max pulled the headline number to the identifying hit.
        Assert.True(
            verdict!.BotProbability >= 0.9,
            $"precondition: sticky-max should carry the 0.98 hit, got {verdict.BotProbability:F3}");

        // Parity with SqliteFingerprintStore.ProjectVerdict -- the compute site behind
        // the read-through that supersedes this seed. Same facts in, same band out, so
        // ApplyResolvedVerdicts cannot visibly change the row.
        var expected = FingerprintRiskProjection
            .Compose(verdict.BotProbability, 0.9, null, "SearchEngine", "sig-crawler")
            .RiskBand.ToString();

        Assert.Equal(expected, verdict.RiskBand);

        // And specifically NOT the window mode -- 9 of 10 rows carried "VeryLow", which
        // is what the majority vote returned beside a 98% probability, rendering
        // "Very Low Risk Profile: 98% bot probability" (operator P0, 2026-08-08).
        Assert.NotEqual("VeryLow", verdict.RiskBand);
    }

    /// <summary>
    ///     The threat pair had the identical defect: ThreatBand was the window MODE while
    ///     the ThreatScore seeded on the same verdict was the LATEST row's score. Both now
    ///     come from the latest row, so they cannot disagree.
    /// </summary>
    [Fact]
    public void Warmed_threat_band_and_score_both_come_from_the_latest_row()
    {
        // Latest row carries a real threat signal; the low-signal bulk behind it does not.
        // Under the old majority vote the bulk's "None" outvoted it and the row rendered a
        // None band beside the latest row's elevated score.
        var window = AssetHeavyCrawlWindow(latestThreatScore: 0.72, latestThreatBand: "High");

        var cache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        cache.WarmFromDetections("sig-crawler", window);

        var verdict = cache.GetResolvedVerdict("sig-crawler");
        Assert.NotNull(verdict);

        Assert.Equal(0.72, verdict!.ThreatScore);
        Assert.Equal("High", verdict.ThreatBand);
    }

    private static DashboardDetectionEvent Detection(
        string primarySignature, double probability, string riskBand, DateTime ts,
        double threatScore, string threatBand) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = ts,
        IsBot = true,
        BotProbability = probability,
        BotType = "SearchEngine",
        PrimarySignature = primarySignature,
        RiskBand = riskBand,
        ThreatScore = threatScore,
        ThreatBand = threatBand,
        Confidence = 0.9,
        Method = "GET",
        Path = "/",
    };
}
