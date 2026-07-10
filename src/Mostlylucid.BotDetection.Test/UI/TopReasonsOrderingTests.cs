using System;
using System.Collections.Generic;
using System.Linq;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Operator report (2026-07-10): the signature-detail "Detection Signals" list showed
///     four near-identical "AI heuristic model: N% bot likelihood (M features)" rows on top
///     and the real reason ("Known bot UA pattern: Bingbot") buried last -- "it's an
///     aggregate NOT the reason". The heuristic is a meta-model that rolls up every other
///     signal, so it must rank BELOW concrete reasons and collapse to a single summary.
/// </summary>
public class TopReasonsOrderingTests
{
    private static DetectionContribution C(string detector, string reason, double delta) => new()
    {
        DetectorName = detector,
        Category = detector,
        ConfidenceDelta = delta,
        Weight = 1.0,
        Reason = reason
    };

    [Fact]
    public void Concrete_reason_ranks_above_heuristic_aggregate_and_duplicates_collapse()
    {
        // The heuristic entries have the HIGHEST weighted score (100%) -- under the old
        // pure-score sort they dominated the top 5 and pushed the UA pattern out.
        var contributions = new List<DetectionContribution>
        {
            C("Heuristic",     "AI heuristic model (late): 100% bot likelihood (298 features)", 1.0),
            C("HeuristicLate", "AI heuristic model (late): 100% bot likelihood (289 features)", 1.0),
            C("Heuristic",     "AI heuristic model (early): 85% bot likelihood (23 features)",  0.85),
            C("HeuristicLate", "AI heuristic model (early): 85% bot likelihood (23 features)",  0.85),
            C("UserAgent",     "Known bot UA pattern: Bingbot",                                 0.7),
        };

        var (topReasons, _) = DetectionBroadcastMiddleware.AggregateContributionsAndTopReasons(contributions);

        // The concrete identity reason surfaces FIRST despite its lower weighted score.
        Assert.Equal("Known bot UA pattern: Bingbot", topReasons[0]);
        // The four heuristic rows collapse to at most one trailing summary line.
        Assert.Single(topReasons, r => r.Contains("heuristic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Heuristic_still_shown_as_the_last_resort_when_it_is_the_only_signal()
    {
        // A request the heuristic flagged with no concrete catalog/attack reason must still
        // surface the heuristic -- demotion must not drop it entirely.
        var contributions = new List<DetectionContribution>
        {
            C("Heuristic",     "AI heuristic model (late): 92% bot likelihood (271 features)", 0.92),
            C("HeuristicLate", "AI heuristic model (late): 92% bot likelihood (260 features)", 0.92),
        };

        var (topReasons, _) = DetectionBroadcastMiddleware.AggregateContributionsAndTopReasons(contributions);

        Assert.Single(topReasons);
        Assert.Contains("heuristic", topReasons[0], StringComparison.OrdinalIgnoreCase);
    }
}
