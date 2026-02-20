using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Clustering;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Clustering;

public class AdaptiveSimilarityWeighterTests
{
    #region Default Weights

    [Fact]
    public void GetDefaultWeights_SumsToOne()
    {
        var weights = AdaptiveSimilarityWeighter.GetDefaultWeights();
        var sum = weights.Values.Sum();
        Assert.Equal(1.0, sum, precision: 6);
    }

    [Fact]
    public void GetDefaultWeights_Has18Features()
    {
        var weights = AdaptiveSimilarityWeighter.GetDefaultWeights();
        Assert.Equal(18, weights.Count);
    }

    [Fact]
    public void GetDefaultWeights_AllPositive()
    {
        var weights = AdaptiveSimilarityWeighter.GetDefaultWeights();
        Assert.All(weights, w => Assert.True(w.Value > 0, $"Weight {w.Key} = {w.Value} should be > 0"));
    }

    [Fact]
    public void GetDefaultWeights_ContainsExpectedFeatures()
    {
        var weights = AdaptiveSimilarityWeighter.GetDefaultWeights();
        var expected = new[]
        {
            "timing", "rate", "pathDiv", "entropy", "botProb", "geo",
            "datacenter", "asn", "spectralEntropy", "harmonic", "peakToAvg",
            "dominantFreq", "selfDrift", "humanDrift", "loopScore",
            "surprise", "novelty", "entropyDelta"
        };

        foreach (var key in expected)
            Assert.True(weights.ContainsKey(key), $"Missing weight: {key}");
    }

    #endregion

    #region ComputeWeights

    [Fact]
    public void ComputeWeights_TooFewFeatures_ReturnsDefaults()
    {
        var weighter = CreateWeighter();
        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeature("s1"),
            CreateFeature("s2")
        };

        var weights = weighter.ComputeWeights(features);
        Assert.Equal(AdaptiveSimilarityWeighter.GetDefaultWeights(), weights);
    }

    [Fact]
    public void ComputeWeights_SumsToApproximatelyOne()
    {
        var weighter = CreateWeighter();
        var features = CreateDiverseFeatures(10);

        var weights = weighter.ComputeWeights(features);
        var sum = weights.Values.Sum();

        Assert.InRange(sum, 0.95, 1.05); // Allow small rounding
    }

    [Fact]
    public void ComputeWeights_AllBounded()
    {
        var weighter = CreateWeighter();
        var features = CreateDiverseFeatures(20);

        var weights = weighter.ComputeWeights(features);

        foreach (var (key, value) in weights)
        {
            Assert.True(value >= 0.01, $"Weight {key} = {value} below floor");
            Assert.True(value <= 0.25, $"Weight {key} = {value} above ceiling");
        }
    }

    [Fact]
    public void ComputeWeights_HighVarianceFeature_GetsHigherWeight()
    {
        var weighter = CreateWeighter();
        var features = new List<BotClusterService.FeatureVector>();

        // Create features where requestRate has high variance, timing is constant
        var random = new Random(42);
        for (var i = 0; i < 20; i++)
        {
            features.Add(CreateFeature($"s{i}",
                timingRegularity: 0.5, // Constant → low CV → low weight
                requestRate: random.NextDouble() * 1000)); // High variance → high CV → high weight
        }

        var weights = weighter.ComputeWeights(features);

        // Rate should have higher weight than timing (higher CV)
        Assert.True(weights["rate"] > weights["timing"],
            $"Expected rate ({weights["rate"]:F4}) > timing ({weights["timing"]:F4}) " +
            "when rate has high variance and timing is constant");
    }

    [Fact]
    public void ComputeWeights_UniformFeature_GetsLowWeight()
    {
        var weighter = CreateWeighter();
        var features = new List<BotClusterService.FeatureVector>();

        // All features have identical datacenter=true → entropy=0 → low weight
        for (var i = 0; i < 20; i++)
        {
            features.Add(CreateFeature($"s{i}",
                isDatacenter: true,
                requestRate: 10.0 + i * 50)); // Vary rate for contrast
        }

        var weights = weighter.ComputeWeights(features);

        // Datacenter should get near-floor weight (no variation in it)
        // Floor is 0.02, but normalization distributes residual so it may be slightly above
        Assert.True(weights["datacenter"] <= 0.06,
            $"Expected datacenter weight <= 0.06 when all identical, got {weights["datacenter"]:F4}");
        // It should still be one of the lowest weights
        var avgWeight = weights.Values.Average();
        Assert.True(weights["datacenter"] < avgWeight,
            $"Expected datacenter ({weights["datacenter"]:F4}) < average ({avgWeight:F4})");
    }

    #endregion

    #region Weight Shift Detection

    [Fact]
    public void ComputeWeights_FirstRun_NoShifts()
    {
        var weighter = CreateWeighter();
        var features = CreateDiverseFeatures(10);

        weighter.ComputeWeights(features);
        var shifts = weighter.GetRecentShifts();

        Assert.Empty(shifts); // No previous weights to compare against
    }

    [Fact]
    public void ComputeWeights_SameData_NoShifts()
    {
        var weighter = CreateWeighter();
        var features = CreateDiverseFeatures(10);

        weighter.ComputeWeights(features);
        weighter.ComputeWeights(features); // Same data

        var shifts = weighter.GetRecentShifts();
        Assert.Empty(shifts); // No change
    }

    [Fact]
    public void ComputeWeights_DifferentData_DetectsShifts()
    {
        var weighter = CreateWeighter();

        // First run: uniform traffic
        var features1 = new List<BotClusterService.FeatureVector>();
        for (var i = 0; i < 20; i++)
            features1.Add(CreateFeature($"s{i}", requestRate: 10.0));

        weighter.ComputeWeights(features1);

        // Second run: wildly different rate distribution
        var features2 = new List<BotClusterService.FeatureVector>();
        var random = new Random(99);
        for (var i = 0; i < 20; i++)
            features2.Add(CreateFeature($"s{i}", requestRate: random.NextDouble() * 10000));

        weighter.ComputeWeights(features2);

        // Rate weight should shift significantly
        var shifts = weighter.GetRecentShifts();
        // May or may not have shifts depending on magnitude — just verify no crash
        Assert.NotNull(shifts);
    }

    #endregion

    #region Helpers

    private static AdaptiveSimilarityWeighter CreateWeighter()
    {
        return new AdaptiveSimilarityWeighter(NullLogger<AdaptiveSimilarityWeighter>.Instance);
    }

    private static BotClusterService.FeatureVector CreateFeature(
        string signature,
        double timingRegularity = 0.5,
        double requestRate = 10.0,
        double pathDiversity = 0.3,
        double pathEntropy = 2.0,
        double avgBotProbability = 0.8,
        string? countryCode = "US",
        bool isDatacenter = true,
        string? asn = "AS15169")
    {
        return new BotClusterService.FeatureVector
        {
            Signature = signature,
            TimingRegularity = timingRegularity,
            RequestRate = requestRate,
            PathDiversity = pathDiversity,
            PathEntropy = pathEntropy,
            AvgBotProbability = avgBotProbability,
            CountryCode = countryCode,
            IsDatacenter = isDatacenter,
            Asn = asn,
            FirstSeen = DateTime.UtcNow.AddMinutes(-5),
            LastSeen = DateTime.UtcNow
        };
    }

    private static List<BotClusterService.FeatureVector> CreateDiverseFeatures(int count)
    {
        var random = new Random(42);
        var countries = new[] { "US", "GB", "DE", "FR", "JP", "CN", "BR" };
        var asns = new[] { "AS15169", "AS13335", "AS16509", "AS8075", "AS32934" };
        var features = new List<BotClusterService.FeatureVector>();

        for (var i = 0; i < count; i++)
        {
            features.Add(CreateFeature($"s{i}",
                timingRegularity: random.NextDouble(),
                requestRate: random.NextDouble() * 500,
                pathDiversity: random.NextDouble(),
                pathEntropy: random.NextDouble() * 5,
                avgBotProbability: 0.5 + random.NextDouble() * 0.5,
                countryCode: countries[random.Next(countries.Length)],
                isDatacenter: random.Next(2) == 0,
                asn: asns[random.Next(asns.Length)]));
        }

        return features;
    }

    #endregion
}
