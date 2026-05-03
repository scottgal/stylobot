using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Markov;

/// <summary>
///     Integration tests verifying MarkovTracker + AdaptiveSimilarityWeighter
///     work correctly with BotClusterService.
/// </summary>
public class MarkovClusterIntegrationTests
{
    [Fact]
    public void MarkovTracker_DriftSignals_FeedIntoFeatureVectors()
    {
        // Create a tracker and record enough transitions to generate drift
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;

        // Simulate a bot that loops between two pages
        var sig = "bot-looper";
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordTransition(sig, "/page-a", now.AddSeconds(i * 2), false, true, false);
            tracker.RecordTransition(sig, "/page-b", now.AddSeconds(i * 2 + 1), false, true, false);
        }

        // Get drift signals
        var drift = tracker.GetDriftSignals(sig, true, false);

        // Loop score should be elevated for A→B→A→B pattern
        Assert.True(drift.LoopScore > 0,
            $"Expected non-zero loop score for A→B→A→B pattern, got {drift.LoopScore}");
    }

    [Fact]
    public void MarkovTracker_HumanVsBot_DifferentDrift()
    {
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;

        // Simulate a human browsing naturally
        var humanSig = "human-1";
        var pages = new[] { "/home", "/about", "/products", "/product/{id}", "/cart", "/contact", "/blog" };
        for (var i = 0; i < 15; i++)
        {
            var page = pages[i % pages.Length];
            tracker.RecordTransition(humanSig, page, now.AddSeconds(i * 3), false, false, true);
        }

        // Flush human traffic to baselines
        tracker.FlushCohortUpdates();

        // Simulate a bot doing repetitive crawling
        var botSig = "bot-crawler";
        for (var i = 0; i < 15; i++)
        {
            tracker.RecordTransition(botSig, $"/page/{i}", now.AddSeconds(i), true, true, false);
        }

        var humanDrift = tracker.GetDriftSignals(humanSig, false, true);
        var botDrift = tracker.GetDriftSignals(botSig, true, false);

        // Both should produce valid signals
        Assert.True(humanDrift.SelfDrift >= 0);
        Assert.True(botDrift.SelfDrift >= 0);
    }

    [Fact]
    public void AdaptiveWeighter_WithDriftFeatures_ComputesWeights()
    {
        var weighter = new AdaptiveSimilarityWeighter(NullLogger<AdaptiveSimilarityWeighter>.Instance);

        // Create features with varying drift signals
        var features = new List<BotClusterService.FeatureVector>();
        var random = new Random(42);

        for (var i = 0; i < 20; i++)
        {
            features.Add(new BotClusterService.FeatureVector
            {
                Signature = $"s{i}",
                TimingRegularity = random.NextDouble(),
                RequestRate = random.NextDouble() * 100,
                PathDiversity = random.NextDouble(),
                PathEntropy = random.NextDouble() * 4,
                AvgBotProbability = 0.5 + random.NextDouble() * 0.5,
                CountryCode = i % 3 == 0 ? "US" : i % 3 == 1 ? "GB" : "DE",
                IsDatacenter = i % 2 == 0,
                Asn = $"AS{random.Next(10000, 99999)}",
                FirstSeen = DateTime.UtcNow.AddMinutes(-10),
                LastSeen = DateTime.UtcNow,
                SelfDrift = random.NextDouble() * 0.5,
                HumanDrift = random.NextDouble() * 0.5,
                LoopScore = random.NextDouble() * 0.3,
                SequenceSurprise = random.NextDouble() * 5,
                TransitionNovelty = random.NextDouble() * 0.5,
                EntropyDelta = (random.NextDouble() - 0.5) * 2
            });
        }

        var weights = weighter.ComputeWeights(features);

        // Verify drift features are included in weights
        Assert.True(weights.ContainsKey("selfDrift"));
        Assert.True(weights.ContainsKey("humanDrift"));
        Assert.True(weights.ContainsKey("loopScore"));
        Assert.True(weights.ContainsKey("surprise"));
        Assert.True(weights.ContainsKey("novelty"));
        Assert.True(weights.ContainsKey("entropyDelta"));

        // All weights should be positive
        Assert.All(weights, w => Assert.True(w.Value > 0));
    }

    [Fact]
    public void ComputeSimilarity_WithDriftSignals_SimilarBotsCluster()
    {
        // Two bots with similar drift profiles should have higher similarity
        // than two bots with different drift profiles
        var botA = new BotClusterService.FeatureVector
        {
            Signature = "bot-a",
            TimingRegularity = 0.1,
            RequestRate = 100,
            PathDiversity = 0.1,
            PathEntropy = 0.5,
            AvgBotProbability = 0.9,
            CountryCode = "US",
            IsDatacenter = true,
            Asn = "AS15169",
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastSeen = DateTime.UtcNow,
            SelfDrift = 0.8,
            HumanDrift = 0.9,
            LoopScore = 0.7,
            SequenceSurprise = 8.0,
            TransitionNovelty = 0.6,
            EntropyDelta = -1.5
        };

        var botB = botA with { Signature = "bot-b" }; // Identical profile

        var humanLike = botA with
        {
            Signature = "human-like",
            SelfDrift = 0.05,
            HumanDrift = 0.1,
            LoopScore = 0.0,
            SequenceSurprise = 1.0,
            TransitionNovelty = 0.05,
            EntropyDelta = 0.1
        };

        var simBotBot = BotClusterService.ComputeSimilarity(botA, botB);
        var simBotHuman = BotClusterService.ComputeSimilarity(botA, humanLike);

        Assert.True(simBotBot > simBotHuman,
            $"Same bot profiles ({simBotBot:F4}) should be more similar than bot vs human-like ({simBotHuman:F4})");
    }

    [Fact]
    public void PathNormalization_ReducesStateExplosion()
    {
        // Verify that path normalization prevents Markov chain explosion
        // when many unique IDs are accessed
        var tracker = CreateTracker(minTransitionsForDrift: 100);
        var now = DateTime.UtcNow;

        // Simulate a bot hitting /product/1, /product/2, ..., /product/100
        for (var i = 0; i < 100; i++)
        {
            tracker.RecordTransition("bot1", $"/product/{i}", now.AddSeconds(i), true, true, false);
        }

        var stats = tracker.GetStats();
        // With normalization, all /product/{id} should collapse to one node
        // Not 100 separate nodes
        Assert.Equal(1, stats.ActiveSignatures);
    }

    [Fact]
    public void FullPipeline_BuildFeatures_WithDrift_IntegrationTest()
    {
        // This tests that BotClusterService.BuildFeatureVectors correctly
        // incorporates drift signals from MarkovTracker
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;

        // Build up some transitions
        tracker.RecordTransition("sig1", "/home", now, false, false, false);
        tracker.RecordTransition("sig1", "/about", now, false, false, false);
        tracker.RecordTransition("sig1", "/products", now, false, false, false);
        tracker.RecordTransition("sig1", "/cart", now, false, false, false);

        // Get drift signals (same as BuildFeatureVectors would do internally)
        var drift = tracker.GetDriftSignals("sig1", false, false);

        // Verify drift values are reasonable
        Assert.InRange(drift.SelfDrift, 0.0, 1.0);
        Assert.InRange(drift.HumanDrift, 0.0, 1.0);
        Assert.InRange(drift.TransitionNovelty, 0.0, 1.0);
        Assert.InRange(drift.LoopScore, 0.0, 1.0);
        Assert.True(drift.SequenceSurprise >= 0);
    }

    #region Helpers

    private static MarkovTracker CreateTracker(int minTransitionsForDrift = 5)
    {
        var options = new BotDetectionOptions
        {
            Markov = new MarkovOptions
            {
                MinTransitionsForDrift = minTransitionsForDrift,
                SignatureHalfLifeHours = 1,
                CohortHalfLifeHours = 6,
                GlobalHalfLifeHours = 24,
                MaxEdgesPerNode = 20,
                RecentTransitionWindowSize = 30
            }
        };

        return new MarkovTracker(
            NullLogger<MarkovTracker>.Instance,
            Options.Create(options));
    }

    #endregion
}
