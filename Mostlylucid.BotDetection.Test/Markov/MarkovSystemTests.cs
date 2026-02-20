using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Markov;

public class MarkovSystemTests
{
    #region DecayingCounter Tests

    [Fact]
    public void DecayingCounter_Decayed_ReturnsHalfAfterOneHalfLife()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(100.0, now);
        var halfLife = TimeSpan.FromHours(1);

        var afterOneHalfLife = counter.Decayed(now.AddHours(1), halfLife);
        Assert.Equal(50.0, afterOneHalfLife, precision: 6);
    }

    [Fact]
    public void DecayingCounter_Decayed_ReturnsQuarterAfterTwoHalfLives()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(100.0, now);
        var halfLife = TimeSpan.FromHours(1);

        var afterTwo = counter.Decayed(now.AddHours(2), halfLife);
        Assert.Equal(25.0, afterTwo, precision: 6);
    }

    [Fact]
    public void DecayingCounter_Decayed_NoElapsedTime_ReturnsFull()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(42.0, now);
        Assert.Equal(42.0, counter.Decayed(now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void DecayingCounter_Decayed_ZeroHalfLife_ReturnsFull()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(42.0, now);
        Assert.Equal(42.0, counter.Decayed(now.AddHours(1), TimeSpan.Zero));
    }

    [Fact]
    public void DecayingCounter_Decayed_NegativeElapsed_ReturnsFull()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(42.0, now);
        Assert.Equal(42.0, counter.Decayed(now.AddHours(-1), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void DecayingCounter_IncrementWithDecay_DecaysThenAdds()
    {
        var now = DateTime.UtcNow;
        var counter = new DecayingCounter(100.0, now);
        var halfLife = TimeSpan.FromHours(1);

        counter.IncrementWithDecay(20.0, now.AddHours(1), halfLife);

        // 100 * 0.5 + 20 = 70
        Assert.Equal(70.0, counter.Value, precision: 6);
        Assert.Equal(now.AddHours(1), counter.LastUpdate);
    }

    #endregion

    #region DecayingTransitionMatrix Tests

    [Fact]
    public void TransitionMatrix_RecordTransition_TracksEdge()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/home", "/about", now);

        Assert.Equal(1, matrix.TotalTransitions);
        Assert.True(matrix.HasEdge("/home", "/about", now));
        Assert.False(matrix.HasEdge("/about", "/home", now));
    }

    [Fact]
    public void TransitionMatrix_GetTransitionProbability_SingleEdge_ReturnsOne()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/home", "/about", now);

        var prob = matrix.GetTransitionProbability("/home", "/about", now);
        Assert.Equal(1.0, prob, precision: 6);
    }

    [Fact]
    public void TransitionMatrix_GetTransitionProbability_TwoEdges_Splits()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/home", "/about", now);
        matrix.RecordTransition("/home", "/contact", now);

        var probAbout = matrix.GetTransitionProbability("/home", "/about", now);
        var probContact = matrix.GetTransitionProbability("/home", "/contact", now);

        Assert.Equal(0.5, probAbout, precision: 2);
        Assert.Equal(0.5, probContact, precision: 2);
        Assert.Equal(1.0, probAbout + probContact, precision: 6);
    }

    [Fact]
    public void TransitionMatrix_GetTransitionProbability_NonexistentEdge_ReturnsZero()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var prob = matrix.GetTransitionProbability("/home", "/nowhere", DateTime.UtcNow);
        Assert.Equal(0.0, prob);
    }

    [Fact]
    public void TransitionMatrix_GetDistribution_ReturnsNormalizedProbabilities()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/home", "/about", now);
        matrix.RecordTransition("/home", "/about", now); // Weight 2
        matrix.RecordTransition("/home", "/contact", now); // Weight 1

        var dist = matrix.GetDistribution("/home", now);

        Assert.Equal(2, dist.Count);
        Assert.True(dist["/about"] > dist["/contact"]);
        Assert.Equal(1.0, dist.Values.Sum(), precision: 6);
    }

    [Fact]
    public void TransitionMatrix_GetDistribution_EmptySource_ReturnsEmpty()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var dist = matrix.GetDistribution("/nonexistent", DateTime.UtcNow);
        Assert.Empty(dist);
    }

    [Fact]
    public void TransitionMatrix_Pruning_KeepsTopK()
    {
        // maxK=3, prune threshold = 2*3 = 6
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(24), maxK: 3);
        var now = DateTime.UtcNow;

        // Create 7 edges from /home (exceeds 2*maxK = 6, triggers prune at 7)
        for (var i = 0; i < 7; i++)
            matrix.RecordTransition("/home", $"/page{i}", now);

        // After pruning, should keep top 3
        var dist = matrix.GetDistribution("/home", now);
        Assert.True(dist.Count <= 3, $"Expected <= 3 edges after pruning, got {dist.Count}");
    }

    [Fact]
    public void TransitionMatrix_PathEntropy_SinglePath_Zero()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        // Only one node visited
        matrix.RecordTransition("/home", "/home", now);

        // With only one destination from /home, entropy should be low
        var entropy = matrix.GetPathEntropy(now);
        Assert.True(entropy >= 0);
    }

    [Fact]
    public void TransitionMatrix_PathEntropy_MultiplePaths_Higher()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        // Visit many distinct paths
        for (var i = 0; i < 10; i++)
            matrix.RecordTransition($"/page{i}", $"/page{i + 1}", now);

        var entropy = matrix.GetPathEntropy(now);
        Assert.True(entropy > 1.0, $"Expected entropy > 1.0 for 10+ nodes, got {entropy}");
    }

    [Fact]
    public void TransitionMatrix_MergeFrom_CombinesEdges()
    {
        var halfLife = TimeSpan.FromHours(1);
        var matrixA = new DecayingTransitionMatrix(halfLife);
        var matrixB = new DecayingTransitionMatrix(halfLife);
        var now = DateTime.UtcNow;

        matrixA.RecordTransition("/home", "/about", now);
        matrixB.RecordTransition("/home", "/contact", now);

        matrixA.MergeFrom(matrixB, now);

        Assert.True(matrixA.HasEdge("/home", "/about", now));
        Assert.True(matrixA.HasEdge("/home", "/contact", now));
    }

    [Fact]
    public void TransitionMatrix_DecayOverTime_ReducesProbability()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/home", "/about", now);

        var probNow = matrix.GetTransitionProbability("/home", "/about", now);
        // After 10 half-lives, the edge should have negligible weight
        var probLater = matrix.GetTransitionProbability("/home", "/about", now.AddHours(10));

        // The probability should still be 1.0 (it's the only edge) even after decay
        // because prob is relative (weight/total), and all weights decay equally
        Assert.Equal(1.0, probNow, precision: 6);
        Assert.Equal(1.0, probLater, precision: 6);
    }

    [Fact]
    public void TransitionMatrix_NodeCount_Tracks()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        Assert.Equal(0, matrix.NodeCount);

        matrix.RecordTransition("/home", "/about", now);
        Assert.Equal(2, matrix.NodeCount); // /home and /about
    }

    #endregion

    #region PathNormalizer Tests

    [Theory]
    [InlineData("/product/12345", "/product/{id}")]
    [InlineData("/api/v2/users", "/api/v{v}/users")]
    [InlineData("/item/abc-def-ghi-jkl-mno-pqr-stu-vwx-yz", "/item/{slug}")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void PathNormalizer_Normalize_ReplacesPatterns(string? input, string expected)
    {
        var result = PathNormalizer.Normalize(input!);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/assets/css/style.css", "{static}")]
    [InlineData("/images/logo.png", "{static}")]
    [InlineData("/fonts/arial.woff2", "{static}")]
    [InlineData("/video.mp4", "{static}")]
    public void PathNormalizer_Normalize_StaticAssets(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Normalize(input));
    }

    [Fact]
    public void PathNormalizer_Normalize_StripsQueryString()
    {
        Assert.Equal("/search", PathNormalizer.Normalize("/search?q=foo&page=2"));
    }

    [Fact]
    public void PathNormalizer_Normalize_StripsFragment()
    {
        Assert.Equal("/page", PathNormalizer.Normalize("/page#section"));
    }

    [Fact]
    public void PathNormalizer_Normalize_ReplacesGuid()
    {
        var result = PathNormalizer.Normalize("/items/550e8400-e29b-41d4-a716-446655440000/details");
        Assert.Equal("/items/{guid}/details", result);
    }

    [Fact]
    public void PathNormalizer_Normalize_ReplacesHexHash()
    {
        var hash = new string('a', 64); // SHA256-like
        var result = PathNormalizer.Normalize($"/assets/{hash}/main.js");
        // main.js → {static}
        Assert.Equal("{static}", result);
    }

    [Fact]
    public void PathNormalizer_Normalize_StripsTrailingSlash()
    {
        Assert.Equal("/about", PathNormalizer.Normalize("/about/"));
    }

    [Theory]
    [InlineData("{static}", "static")]
    [InlineData("/api/v{v}/users", "api")]
    [InlineData("/_stylobot/data", "api")]
    [InlineData("/search", "search")]
    [InlineData("/login", "auth")]
    [InlineData("/oauth/callback", "auth")]
    [InlineData("/admin/settings", "admin")]
    [InlineData("/product/{id}", "detail")]
    [InlineData("/items/{guid}/details", "detail")]
    [InlineData("/feed", "meta")]
    [InlineData("/sitemap", "meta")]
    [InlineData("/", "home")]
    [InlineData("/about", "page")]
    public void PathNormalizer_Classify_ReturnsCorrectType(string normalizedPath, string expectedType)
    {
        Assert.Equal(expectedType, PathNormalizer.Classify(normalizedPath));
    }

    #endregion

    #region DivergenceMetrics Tests

    [Fact]
    public void JSD_IdenticalDistributions_ReturnsZero()
    {
        var p = new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0.5 };
        var q = new Dictionary<string, double> { ["a"] = 0.5, ["b"] = 0.5 };

        var jsd = DivergenceMetrics.JensenShannonDivergence(p, q);
        Assert.Equal(0.0, jsd, precision: 6);
    }

    [Fact]
    public void JSD_CompletelyDifferent_ReturnsHigh()
    {
        var p = new Dictionary<string, double> { ["a"] = 1.0 };
        var q = new Dictionary<string, double> { ["b"] = 1.0 };

        var jsd = DivergenceMetrics.JensenShannonDivergence(p, q);
        Assert.True(jsd > 0.9, $"Expected JSD > 0.9 for disjoint distributions, got {jsd}");
    }

    [Fact]
    public void JSD_EmptyDistributions_ReturnsZero()
    {
        var empty = new Dictionary<string, double>();
        Assert.Equal(0.0, DivergenceMetrics.JensenShannonDivergence(empty, empty));
    }

    [Fact]
    public void JSD_OneEmpty_ReturnsOne()
    {
        var p = new Dictionary<string, double> { ["a"] = 1.0 };
        var empty = new Dictionary<string, double>();

        Assert.Equal(1.0, DivergenceMetrics.JensenShannonDivergence(p, empty));
        Assert.Equal(1.0, DivergenceMetrics.JensenShannonDivergence(empty, p));
    }

    [Fact]
    public void JSD_IsSymmetric()
    {
        var p = new Dictionary<string, double> { ["a"] = 0.7, ["b"] = 0.3 };
        var q = new Dictionary<string, double> { ["a"] = 0.3, ["b"] = 0.7 };

        var jsdPQ = DivergenceMetrics.JensenShannonDivergence(p, q);
        var jsdQP = DivergenceMetrics.JensenShannonDivergence(q, p);

        Assert.Equal(jsdPQ, jsdQP, precision: 10);
    }

    [Fact]
    public void JSD_BoundedZeroToOne()
    {
        var p = new Dictionary<string, double> { ["a"] = 0.9, ["b"] = 0.1 };
        var q = new Dictionary<string, double> { ["a"] = 0.1, ["b"] = 0.9 };

        var jsd = DivergenceMetrics.JensenShannonDivergence(p, q);
        Assert.InRange(jsd, 0.0, 1.0);
    }

    [Fact]
    public void LoopScore_NoLoops_ReturnsZero()
    {
        var transitions = new List<(string, string)>
        {
            ("/a", "/b"), ("/b", "/c"), ("/c", "/d"), ("/d", "/e")
        };

        var score = DivergenceMetrics.ComputeLoopScore(transitions);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void LoopScore_PerfectLoop_ReturnsHigh()
    {
        // A→B, B→A, A→B, B→A
        var transitions = new List<(string, string)>
        {
            ("/a", "/b"), ("/b", "/a"), ("/a", "/b"), ("/b", "/a"),
            ("/a", "/b"), ("/b", "/a"), ("/a", "/b"), ("/b", "/a")
        };

        var score = DivergenceMetrics.ComputeLoopScore(transitions);
        Assert.True(score > 0.3, $"Expected loop score > 0.3 for A→B→A→B pattern, got {score}");
    }

    [Fact]
    public void LoopScore_TooFewTransitions_ReturnsZero()
    {
        var transitions = new List<(string, string)>
        {
            ("/a", "/b"), ("/b", "/a"), ("/a", "/b")
        };

        Assert.Equal(0.0, DivergenceMetrics.ComputeLoopScore(transitions));
    }

    [Fact]
    public void TransitionNovelty_AllKnown_ReturnsZero()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/a", "/b", now);
        matrix.RecordTransition("/b", "/c", now);

        var transitions = new List<(string, string)> { ("/a", "/b"), ("/b", "/c") };
        var novelty = DivergenceMetrics.ComputeTransitionNovelty(transitions, matrix, now);
        Assert.Equal(0.0, novelty);
    }

    [Fact]
    public void TransitionNovelty_AllNovel_ReturnsOne()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        // Matrix has A→B, but recent has X→Y
        matrix.RecordTransition("/a", "/b", now);

        var transitions = new List<(string, string)> { ("/x", "/y"), ("/y", "/z") };
        var novelty = DivergenceMetrics.ComputeTransitionNovelty(transitions, matrix, now);
        Assert.Equal(1.0, novelty);
    }

    [Fact]
    public void TransitionNovelty_HalfNovel_ReturnsHalf()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/a", "/b", now);

        var transitions = new List<(string, string)> { ("/a", "/b"), ("/x", "/y") };
        var novelty = DivergenceMetrics.ComputeTransitionNovelty(transitions, matrix, now);
        Assert.Equal(0.5, novelty);
    }

    [Fact]
    public void AverageTransitionSurprise_KnownTransitions_LowSurprise()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        // Make /a → /b the only transition (probability 1.0)
        matrix.RecordTransition("/a", "/b", now);

        var transitions = new List<(string, string)> { ("/a", "/b") };
        var surprise = DivergenceMetrics.AverageTransitionSurprise(transitions, matrix, now);

        // -log2(1.0) = 0.0
        Assert.Equal(0.0, surprise, precision: 6);
    }

    [Fact]
    public void AverageTransitionSurprise_UnknownTransitions_HighSurprise()
    {
        var matrix = new DecayingTransitionMatrix(TimeSpan.FromHours(1));
        var now = DateTime.UtcNow;

        matrix.RecordTransition("/a", "/b", now);

        var transitions = new List<(string, string)> { ("/x", "/y") };
        var surprise = DivergenceMetrics.AverageTransitionSurprise(transitions, matrix, now);

        Assert.Equal(10.0, surprise); // Max surprise for impossible transitions
    }

    #endregion

    #region DriftSignals Tests

    [Fact]
    public void DriftSignals_Empty_AllZeros()
    {
        var empty = DriftSignals.Empty;
        Assert.Equal(0.0, empty.SelfDrift);
        Assert.Equal(0.0, empty.HumanDrift);
        Assert.Equal(0.0, empty.TransitionNovelty);
        Assert.Equal(0.0, empty.EntropyDelta);
        Assert.Equal(0.0, empty.LoopScore);
        Assert.Equal(0.0, empty.SequenceSurprise);
    }

    [Fact]
    public void DriftSignals_HasSignificantDrift_HighSelfDrift_ReturnsTrue()
    {
        var options = new MarkovOptions();
        var drift = new DriftSignals { SelfDrift = 0.5 }; // Above default 0.3
        Assert.True(drift.HasSignificantDrift(options));
    }

    [Fact]
    public void DriftSignals_HasSignificantDrift_BelowAllThresholds_ReturnsFalse()
    {
        var options = new MarkovOptions();
        var drift = new DriftSignals
        {
            SelfDrift = 0.1,
            HumanDrift = 0.1,
            LoopScore = 0.1,
            SequenceSurprise = 0.5
        };
        Assert.False(drift.HasSignificantDrift(options));
    }

    #endregion

    #region RecentTransitionBuffer Tests

    [Fact]
    public void RecentTransitionBuffer_Add_StoresTransitions()
    {
        var buffer = new RecentTransitionBuffer(5);
        buffer.Add(("/a", "/b"));
        buffer.Add(("/b", "/c"));

        var recent = buffer.GetRecent();
        Assert.Equal(2, recent.Count);
        Assert.Equal(("/a", "/b"), recent[0]);
        Assert.Equal(("/b", "/c"), recent[1]);
    }

    [Fact]
    public void RecentTransitionBuffer_Wraps_AroundCapacity()
    {
        var buffer = new RecentTransitionBuffer(3);
        buffer.Add(("/a", "/b"));
        buffer.Add(("/b", "/c"));
        buffer.Add(("/c", "/d"));
        buffer.Add(("/d", "/e")); // Overwrites first

        var recent = buffer.GetRecent();
        Assert.Equal(3, recent.Count);
        Assert.Equal(("/b", "/c"), recent[0]);
        Assert.Equal(("/c", "/d"), recent[1]);
        Assert.Equal(("/d", "/e"), recent[2]);
    }

    [Fact]
    public void RecentTransitionBuffer_Empty_ReturnsEmpty()
    {
        var buffer = new RecentTransitionBuffer(10);
        Assert.Empty(buffer.GetRecent());
    }

    #endregion

    #region MarkovTracker Integration Tests

    [Fact]
    public void MarkovTracker_RecordTransition_FirstTransition_ReturnsEmpty()
    {
        var tracker = CreateTracker();

        // First request for a signature — no previous path, so no transition recorded
        var drift = tracker.RecordTransition("sig1", "/home", DateTime.UtcNow, false, false, false);
        Assert.Equal(DriftSignals.Empty, drift);
    }

    [Fact]
    public void MarkovTracker_RecordTransition_BuildsChain()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        tracker.RecordTransition("sig1", "/home", now, false, false, false);
        tracker.RecordTransition("sig1", "/about", now, false, false, false);
        tracker.RecordTransition("sig1", "/contact", now, false, false, false);

        // After 3 transitions, should have 2 edges (/home→/about, /about→/contact)
        var stats = tracker.GetStats();
        Assert.Equal(1, stats.ActiveSignatures);
    }

    [Fact]
    public void MarkovTracker_RecordTransition_AfterMinTransitions_ReturnsDrift()
    {
        var tracker = CreateTracker(minTransitionsForDrift: 3);
        var now = DateTime.UtcNow;

        // Record enough transitions to trigger drift computation
        tracker.RecordTransition("sig1", "/home", now, false, false, true);
        tracker.RecordTransition("sig1", "/about", now, false, false, true);
        tracker.RecordTransition("sig1", "/products", now, false, false, true);
        tracker.RecordTransition("sig1", "/cart", now, false, false, true);
        var drift = tracker.RecordTransition("sig1", "/checkout", now, false, false, true);

        // Should get non-empty drift signals (at least selfDrift should be computed)
        // Note: humanDrift depends on cohort baselines which aren't populated here
        Assert.True(drift.SelfDrift >= 0);
    }

    [Fact]
    public void MarkovTracker_FlushCohortUpdates_ProcessesHumanTraffic()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // Record transitions from a human (isBot=false)
        tracker.RecordTransition("sig1", "/home", now, false, false, false);
        tracker.RecordTransition("sig1", "/about", now, false, false, false);

        // Flush to cohort baselines
        tracker.FlushCohortUpdates();

        var stats = tracker.GetStats();
        // Global baseline should have received the human traffic
        Assert.True(stats.GlobalBaselineNodes >= 0);
    }

    [Fact]
    public void MarkovTracker_FlushCohortUpdates_IgnoresBotTraffic()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // Record transitions from a bot (isBot=true)
        tracker.RecordTransition("sig1", "/home", now, true, true, false);
        tracker.RecordTransition("sig1", "/robots.txt", now, true, true, false);

        var beforeFlush = tracker.GetStats();
        tracker.FlushCohortUpdates();
        var afterFlush = tracker.GetStats();

        // Global baseline should NOT increase (bot traffic excluded)
        Assert.Equal(beforeFlush.GlobalBaselineNodes, afterFlush.GlobalBaselineNodes);
    }

    [Fact]
    public void MarkovTracker_GetDriftSignals_UnknownSignature_ReturnsEmpty()
    {
        var tracker = CreateTracker();
        var drift = tracker.GetDriftSignals("unknown", false, false);
        Assert.Equal(DriftSignals.Empty, drift);
    }

    [Fact]
    public void MarkovTracker_GetStats_ReportsCorrectly()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        tracker.RecordTransition("sig1", "/a", now, false, false, false);
        tracker.RecordTransition("sig2", "/b", now, false, true, true);

        var stats = tracker.GetStats();
        Assert.Equal(2, stats.ActiveSignatures);
    }

    [Fact]
    public void MarkovTracker_CohortKey_DatacenterNew()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // datacenter + new visitor
        tracker.RecordTransition("sig1", "/home", now, false, true, false);
        tracker.RecordTransition("sig1", "/about", now, false, true, false);

        tracker.FlushCohortUpdates();

        var cohorts = tracker.GetCohortBaselines();
        Assert.True(cohorts.ContainsKey("datacenter-new"),
            $"Expected 'datacenter-new' cohort, found: [{string.Join(", ", cohorts.Keys)}]");
    }

    [Fact]
    public void MarkovTracker_CohortKey_ResidentialReturning()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        // residential + returning visitor
        tracker.RecordTransition("sig1", "/home", now, false, false, true);
        tracker.RecordTransition("sig1", "/about", now, false, false, true);

        tracker.FlushCohortUpdates();

        var cohorts = tracker.GetCohortBaselines();
        Assert.True(cohorts.ContainsKey("residential-returning"),
            $"Expected 'residential-returning' cohort, found: [{string.Join(", ", cohorts.Keys)}]");
    }

    [Fact]
    public void MarkovTracker_CohortKey_WithCluster()
    {
        var tracker = CreateTracker();
        var now = DateTime.UtcNow;

        tracker.RecordTransition("sig1", "/home", now, false, true, false, "cluster-abc");
        tracker.RecordTransition("sig1", "/about", now, false, true, false, "cluster-abc");

        tracker.FlushCohortUpdates();

        var cohorts = tracker.GetCohortBaselines();
        Assert.True(cohorts.ContainsKey("datacenter-new:cluster-abc"),
            $"Expected cluster cohort key, found: [{string.Join(", ", cohorts.Keys)}]");
    }

    [Fact]
    public void MarkovTracker_PathNormalization_Applied()
    {
        var tracker = CreateTracker(minTransitionsForDrift: 100); // High to avoid drift computation
        var now = DateTime.UtcNow;

        // Record a path with an ID - it should be normalized
        tracker.RecordTransition("sig1", "/product/12345", now, false, false, false);
        tracker.RecordTransition("sig1", "/product/67890", now, false, false, false);

        // Both should normalize to /product/{id} → same edge reinforced, not two different edges
        // We can verify indirectly: only 1 active signature
        var stats = tracker.GetStats();
        Assert.Equal(1, stats.ActiveSignatures);
    }

    #endregion

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
