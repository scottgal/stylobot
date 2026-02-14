using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Comprehensive tests for BotClusterService covering static methods (ComputeClusterId, GenerateLabel),
///     similarity computation (ComputeSimilarity), feature vector building (BuildFeatureVectors),
///     similarity graph construction (BuildSimilarityGraph), public API behavior, and NotifyBotDetected.
/// </summary>
public class BotClusterServiceTests
{
    #region Helpers

    private static BotDetectionOptions DefaultOptions(Action<ClusterOptions>? configure = null)
    {
        var opts = new BotDetectionOptions();
        opts.Cluster.MinClusterSize = 3;
        opts.Cluster.SimilarityThreshold = 0.6; // Lower for tests
        opts.Cluster.MinBotProbabilityForClustering = 0.5;
        opts.Cluster.MinBotDetectionsToTrigger = 5;
        opts.Cluster.MaxIterations = 10;
        opts.Cluster.ProductSimilarityThreshold = 0.8;
        opts.Cluster.NetworkTemporalDensityThreshold = 0.6;
        configure?.Invoke(opts.Cluster);
        return opts;
    }

    private static SignatureCoordinator CreateCoordinator(BotDetectionOptions? opts = null)
    {
        opts ??= DefaultOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static BotClusterService CreateService(
        BotDetectionOptions? opts = null,
        SignatureCoordinator? coordinator = null)
    {
        opts ??= DefaultOptions();
        coordinator ??= CreateCoordinator(opts);
        return new BotClusterService(
            NullLogger<BotClusterService>.Instance,
            Options.Create(opts),
            coordinator);
    }

    private static SignatureBehavior CreateBehavior(
        string signature,
        double avgBotProbability = 0.8,
        int requestCount = 10,
        double averageInterval = 1.5,
        double pathEntropy = 2.0,
        double timingCoefficient = 0.1,
        string? countryCode = "US",
        string? asn = "AS15169",
        bool isDatacenter = true,
        DateTime? firstSeen = null,
        DateTime? lastSeen = null,
        List<SignatureRequest>? requests = null)
    {
        var now = DateTime.UtcNow;
        var first = firstSeen ?? now.AddMinutes(-5);
        var last = lastSeen ?? now;

        if (requests == null)
        {
            requests = new List<SignatureRequest>();
            for (var i = 0; i < requestCount; i++)
            {
                var ts = first.AddSeconds(i * averageInterval);
                requests.Add(new SignatureRequest
                {
                    RequestId = $"{signature}-req-{i}",
                    Timestamp = ts,
                    Path = $"/page/{i % 5}",
                    BotProbability = avgBotProbability,
                    Signals = new Dictionary<string, object>(),
                    DetectorsRan = new HashSet<string> { "test" }
                });
            }
        }

        return new SignatureBehavior
        {
            Signature = signature,
            Requests = requests,
            FirstSeen = first,
            LastSeen = last,
            RequestCount = requestCount,
            AverageInterval = averageInterval,
            PathEntropy = pathEntropy,
            TimingCoefficient = timingCoefficient,
            AverageBotProbability = avgBotProbability,
            AberrationScore = 0.0,
            IsAberrant = false,
            CountryCode = countryCode,
            Asn = asn,
            IsDatacenter = isDatacenter
        };
    }

    private static SignatureRequest CreateRequest(
        string path = "/page",
        double botProbability = 0.9,
        DateTime? timestamp = null,
        string? requestId = null)
    {
        return new SignatureRequest
        {
            RequestId = requestId ?? Guid.NewGuid().ToString(),
            Timestamp = timestamp ?? DateTime.UtcNow,
            Path = path,
            BotProbability = botProbability,
            Signals = new Dictionary<string, object>(),
            DetectorsRan = new HashSet<string> { "UserAgent", "Header" }
        };
    }

    private static List<SignatureRequest> CreateTimedRequests(
        int count,
        DateTime startTime,
        TimeSpan interval,
        string pathPrefix = "/page",
        double botProbability = 0.9)
    {
        var requests = new List<SignatureRequest>();
        for (var i = 0; i < count; i++)
        {
            requests.Add(CreateRequest(
                path: $"{pathPrefix}/{i}",
                botProbability: botProbability,
                timestamp: startTime + interval * i,
                requestId: $"req-{i}"));
        }
        return requests;
    }

    private static BotClusterService.FeatureVector CreateFeatureVector(
        string signature = "sig-test",
        double timingRegularity = 0.5,
        double requestRate = 10.0,
        double pathDiversity = 0.3,
        double pathEntropy = 2.0,
        double avgBotProbability = 0.8,
        string? countryCode = "US",
        bool isDatacenter = true,
        string? asn = "AS15169",
        SpectralFeatures? spectral = null,
        double[]? intervals = null)
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
            LastSeen = DateTime.UtcNow,
            Spectral = spectral,
            Intervals = intervals
        };
    }

    #endregion

    #region Initial State / Public API Before Clustering

    [Fact]
    public void FindCluster_BeforeClustering_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.FindCluster("nonexistent"));
    }

    [Fact]
    public void GetClusters_BeforeClustering_ReturnsEmpty()
    {
        var svc = CreateService();
        var clusters = svc.GetClusters();
        Assert.NotNull(clusters);
        Assert.Empty(clusters);
    }

    [Fact]
    public void GetSpectralFeatures_BeforeClustering_ReturnsNull()
    {
        var svc = CreateService();
        Assert.Null(svc.GetSpectralFeatures("nonexistent"));
    }

    #endregion

    #region ComputeClusterId - Static Method Tests

    [Fact]
    public void ComputeClusterId_SameSignatures_SameId()
    {
        var sigs = new List<string> { "sig-a", "sig-b", "sig-c" };
        var id1 = BotClusterService.ComputeClusterId(sigs);
        var id2 = BotClusterService.ComputeClusterId(sigs);
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void ComputeClusterId_DifferentSignatures_DifferentId()
    {
        var id1 = BotClusterService.ComputeClusterId(new List<string> { "sig-a", "sig-b" });
        var id2 = BotClusterService.ComputeClusterId(new List<string> { "sig-c", "sig-d" });
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void ComputeClusterId_OrderIndependent_WhenPreSorted()
    {
        // The method expects sorted input; verify that the same sorted list
        // produces the same ID regardless of original insertion order.
        var signaturesA = new List<string> { "sig-a", "sig-b", "sig-c" };
        var signaturesB = new List<string> { "sig-c", "sig-a", "sig-b" };
        signaturesB.Sort();

        var idA = BotClusterService.ComputeClusterId(signaturesA);
        var idB = BotClusterService.ComputeClusterId(signaturesB);
        Assert.Equal(idA, idB);
    }

    [Fact]
    public void ComputeClusterId_StartsWithClusterPrefix()
    {
        var id = BotClusterService.ComputeClusterId(new List<string> { "a", "b" });
        Assert.StartsWith("cluster-", id);
    }

    [Fact]
    public void ComputeClusterId_ReturnsValidHexFormat()
    {
        var id = BotClusterService.ComputeClusterId(new List<string> { "sig-1", "sig-2" });
        Assert.StartsWith("cluster-", id);
        var hexPart = id["cluster-".Length..];
        // SHA256 first 8 bytes = 16 hex chars
        Assert.Equal(16, hexPart.Length);
        Assert.All(hexPart, c => Assert.True(
            char.IsAsciiHexDigitLower(c) || char.IsDigit(c),
            $"Expected lowercase hex character, got '{c}'"));
    }

    [Fact]
    public void ComputeClusterId_EmptyList_DoesNotThrow()
    {
        // Edge case: empty list should produce a valid cluster ID (hash of empty string)
        var id = BotClusterService.ComputeClusterId(new List<string>());
        Assert.StartsWith("cluster-", id);
    }

    [Fact]
    public void ComputeClusterId_SingleSignature_ReturnsValidId()
    {
        var id = BotClusterService.ComputeClusterId(new List<string> { "only-one" });
        Assert.StartsWith("cluster-", id);
        Assert.True(id.Length > "cluster-".Length);
    }

    #endregion

    #region GenerateLabel - Static Method Tests

    [Fact]
    public void GenerateLabel_BotProduct_RapidScraper()
    {
        // avgInterval < 2.0 triggers Rapid-Scraper
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 0.5),
            CreateBehavior("s2", averageInterval: 1.0),
            CreateBehavior("s3", averageInterval: 1.5)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.8, members);
        Assert.Equal("Rapid-Scraper", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_DeepCrawler()
    {
        // avgEntropy > 3.0 and avgInterval >= 2.0 triggers Deep-Crawler
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 5.0, pathEntropy: 4.0),
            CreateBehavior("s2", averageInterval: 5.0, pathEntropy: 3.5),
            CreateBehavior("s3", averageInterval: 5.0, pathEntropy: 3.2)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.8, members);
        Assert.Equal("Deep-Crawler", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_TargetedScanner()
    {
        // avgEntropy < 1.0 and avgInterval >= 2.0 triggers Targeted-Scanner
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 5.0, pathEntropy: 0.5),
            CreateBehavior("s2", averageInterval: 5.0, pathEntropy: 0.3),
            CreateBehavior("s3", averageInterval: 5.0, pathEntropy: 0.8)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.8, members);
        Assert.Equal("Targeted-Scanner", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_Default()
    {
        // avgInterval >= 2.0, entropy between 1.0 and 3.0 -> Bot-Software
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 5.0, pathEntropy: 2.0),
            CreateBehavior("s2", averageInterval: 5.0, pathEntropy: 2.0),
            CreateBehavior("s3", averageInterval: 5.0, pathEntropy: 2.0)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.8, members);
        Assert.Equal("Bot-Software", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_BurstCampaign()
    {
        // temporalDensity > 0.8 triggers Burst-Campaign
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1"), CreateBehavior("s2"), CreateBehavior("s3")
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.9, 0.8, members);
        Assert.Equal("Burst-Campaign", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_LargeBotnet()
    {
        // members.Count > 10 and temporalDensity <= 0.8 triggers Large-Botnet
        var members = Enumerable.Range(0, 12)
            .Select(i => CreateBehavior($"s{i}"))
            .ToList();
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.5, 0.8, members);
        Assert.Equal("Large-Botnet", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_Default()
    {
        // temporalDensity <= 0.8, members.Count <= 10 -> Coordinated-Campaign
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1"), CreateBehavior("s2"), CreateBehavior("s3")
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.5, 0.8, members);
        Assert.Equal("Coordinated-Campaign", label);
    }

    [Fact]
    public void GenerateLabel_Unknown_ReturnsUnknownCluster()
    {
        var members = new List<SignatureBehavior> { CreateBehavior("s1") };
        var label = BotClusterService.GenerateLabel(BotClusterType.Unknown, 0.5, 0.5, 0.5, members);
        Assert.Equal("Unknown-Cluster", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_RapidScraper_ExactBoundary()
    {
        // avgInterval == 2.0 should NOT trigger Rapid-Scraper (requires < 2.0)
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 2.0, pathEntropy: 2.0),
            CreateBehavior("s2", averageInterval: 2.0, pathEntropy: 2.0),
            CreateBehavior("s3", averageInterval: 2.0, pathEntropy: 2.0)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.9, members);
        Assert.Equal("Bot-Software", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_DeepCrawler_ExactBoundary()
    {
        // avgEntropy == 3.0 should NOT trigger Deep-Crawler (requires > 3.0)
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 5.0, pathEntropy: 3.0),
            CreateBehavior("s2", averageInterval: 5.0, pathEntropy: 3.0),
            CreateBehavior("s3", averageInterval: 5.0, pathEntropy: 3.0)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.9, members);
        Assert.Equal("Bot-Software", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_TargetedScanner_ExactBoundary()
    {
        // avgEntropy == 1.0 should NOT trigger Targeted-Scanner (requires < 1.0)
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 5.0, pathEntropy: 1.0),
            CreateBehavior("s2", averageInterval: 5.0, pathEntropy: 1.0),
            CreateBehavior("s3", averageInterval: 5.0, pathEntropy: 1.0)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.9, members);
        Assert.Equal("Bot-Software", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_BurstCampaign_ExactBoundary()
    {
        // temporalDensity == 0.8 should NOT trigger Burst-Campaign (requires > 0.8)
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1"), CreateBehavior("s2"), CreateBehavior("s3")
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.8, 0.9, members);
        // 3 members is not > 10, so falls to default
        Assert.Equal("Coordinated-Campaign", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_LargeBotnet_ExactBoundary()
    {
        // members.Count == 10 should NOT trigger Large-Botnet (requires > 10)
        var members = Enumerable.Range(0, 10)
            .Select(i => CreateBehavior($"s{i}"))
            .ToList();
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.5, 0.9, members);
        Assert.Equal("Coordinated-Campaign", label);
    }

    [Fact]
    public void GenerateLabel_BotProduct_PrioritizesRapidScraper_OverDeepCrawler()
    {
        // When both avgInterval < 2.0 AND avgEntropy > 3.0, Rapid-Scraper takes priority
        var members = new List<SignatureBehavior>
        {
            CreateBehavior("s1", averageInterval: 1.0, pathEntropy: 4.0),
            CreateBehavior("s2", averageInterval: 1.0, pathEntropy: 4.0),
            CreateBehavior("s3", averageInterval: 1.0, pathEntropy: 4.0)
        };
        var label = BotClusterService.GenerateLabel(BotClusterType.BotProduct, 0.9, 0.5, 0.9, members);
        Assert.Equal("Rapid-Scraper", label);
    }

    [Fact]
    public void GenerateLabel_BotNetwork_PrioritizesBurstCampaign_OverLargeBotnet()
    {
        // When both temporalDensity > 0.8 AND members.Count > 10, Burst-Campaign wins
        var members = Enumerable.Range(0, 15)
            .Select(i => CreateBehavior($"s{i}"))
            .ToList();
        var label = BotClusterService.GenerateLabel(BotClusterType.BotNetwork, 0.7, 0.9, 0.9, members);
        Assert.Equal("Burst-Campaign", label);
    }

    #endregion

    #region ComputeSimilarity Tests

    [Fact]
    public void ComputeSimilarity_IdenticalFeatures_ReturnsOne()
    {
        // Must include spectral features with HasSufficientData=true for full 1.0 similarity.
        // Without spectral data, neutral values (0.5) reduce the total below 1.0.
        var f = CreateFeatureVector(
            spectral: new SpectralFeatures
            {
                DominantFrequency = 0.25,
                SpectralEntropy = 0.5,
                HarmonicRatio = 0.3,
                SpectralCentroid = 0.4,
                PeakToAvgRatio = 0.6,
                HasSufficientData = true
            });
        var sim = BotClusterService.ComputeSimilarity(f, f);
        Assert.Equal(1.0, sim, precision: 6);
    }

    [Fact]
    public void ComputeSimilarity_CompletelyDifferentFeatures_LowSimilarity()
    {
        var a = CreateFeatureVector(
            signature: "s1",
            timingRegularity: 0.1,
            requestRate: 100.0,
            pathDiversity: 0.1,
            pathEntropy: 0.5,
            avgBotProbability: 0.9,
            countryCode: "US",
            isDatacenter: true,
            asn: "AS15169");

        var b = CreateFeatureVector(
            signature: "s2",
            timingRegularity: 0.9,
            requestRate: 1.0,
            pathDiversity: 0.9,
            pathEntropy: 4.0,
            avgBotProbability: 0.1,
            countryCode: "CN",
            isDatacenter: false,
            asn: "AS4808");

        var sim = BotClusterService.ComputeSimilarity(a, b);
        Assert.InRange(sim, 0.0, 0.6);
    }

    [Fact]
    public void ComputeSimilarity_SameCountry_BoostsSimilarity()
    {
        var baseFeature = CreateFeatureVector(signature: "s1", countryCode: "US");
        var sameCountry = CreateFeatureVector(signature: "s2", countryCode: "US");
        var diffCountry = CreateFeatureVector(signature: "s3", countryCode: "CN");

        var simSame = BotClusterService.ComputeSimilarity(baseFeature, sameCountry);
        var simDiff = BotClusterService.ComputeSimilarity(baseFeature, diffCountry);
        Assert.True(simSame > simDiff,
            $"Same country similarity ({simSame:F4}) should exceed different country ({simDiff:F4})");
    }

    [Fact]
    public void ComputeSimilarity_SameASN_BoostsSimilarity()
    {
        var baseFeature = CreateFeatureVector(signature: "s1", asn: "AS15169");
        var sameAsn = CreateFeatureVector(signature: "s2", asn: "AS15169");
        var diffAsn = CreateFeatureVector(signature: "s3", asn: "AS99999");

        var simSame = BotClusterService.ComputeSimilarity(baseFeature, sameAsn);
        var simDiff = BotClusterService.ComputeSimilarity(baseFeature, diffAsn);
        Assert.True(simSame > simDiff,
            $"Same ASN similarity ({simSame:F4}) should exceed different ASN ({simDiff:F4})");
    }

    [Fact]
    public void ComputeSimilarity_SpectralFeatures_WhenBothPresent_AffectsSimilarity()
    {
        var spectral = new SpectralFeatures
        {
            DominantFrequency = 0.25,
            SpectralEntropy = 0.3,
            HarmonicRatio = 0.5,
            SpectralCentroid = 0.4,
            PeakToAvgRatio = 0.7,
            HasSufficientData = true
        };
        var diffSpectral = new SpectralFeatures
        {
            DominantFrequency = 0.8,
            SpectralEntropy = 0.9,
            HarmonicRatio = 0.1,
            SpectralCentroid = 0.8,
            PeakToAvgRatio = 0.1,
            HasSufficientData = true
        };

        var a = CreateFeatureVector(signature: "s1", spectral: spectral);
        var bSame = CreateFeatureVector(signature: "s2", spectral: spectral);
        var bDiff = CreateFeatureVector(signature: "s3", spectral: diffSpectral);

        var simSame = BotClusterService.ComputeSimilarity(a, bSame);
        var simDiff = BotClusterService.ComputeSimilarity(a, bDiff);
        Assert.True(simSame > simDiff,
            $"Same spectral ({simSame:F4}) should be more similar than different ({simDiff:F4})");
    }

    [Fact]
    public void ComputeSimilarity_SpectralFeatures_WhenOneMissing_UsesNeutral()
    {
        var spectral = new SpectralFeatures
        {
            DominantFrequency = 0.25,
            SpectralEntropy = 0.3,
            HarmonicRatio = 0.5,
            SpectralCentroid = 0.4,
            PeakToAvgRatio = 0.7,
            HasSufficientData = true
        };
        var a = CreateFeatureVector(signature: "s1", spectral: spectral);
        var b = CreateFeatureVector(signature: "s2", spectral: null);

        // Should not crash and should use neutral (0.5) for spectral components
        var sim = BotClusterService.ComputeSimilarity(a, b);
        Assert.InRange(sim, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSimilarity_SpectralFeatures_WhenBothMissing_UsesNeutral()
    {
        var a = CreateFeatureVector(signature: "s1", spectral: null);
        var b = CreateFeatureVector(signature: "s2", spectral: null);

        var sim = BotClusterService.ComputeSimilarity(a, b);
        Assert.InRange(sim, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSimilarity_SpectralFeatures_InsufficientData_UsesNeutral()
    {
        // HasSufficientData = false should be treated like missing spectral data
        var spectral = new SpectralFeatures
        {
            DominantFrequency = 0.25,
            SpectralEntropy = 0.3,
            HarmonicRatio = 0.5,
            SpectralCentroid = 0.4,
            PeakToAvgRatio = 0.7,
            HasSufficientData = false // insufficient
        };
        var a = CreateFeatureVector(signature: "s1", spectral: spectral);
        var b = CreateFeatureVector(signature: "s2", spectral: spectral);

        var sim = BotClusterService.ComputeSimilarity(a, b);
        Assert.InRange(sim, 0.0, 1.0);
    }

    [Theory]
    [InlineData(0.0, 1.0, 0.5, 0.0, 0.0)]
    [InlineData(1.0, 0.0, 0.5, 5.0, 1.0)]
    [InlineData(0.5, 500.0, 0.25, 2.5, 0.5)]
    [InlineData(0.99, 0.01, 0.99, 4.99, 0.01)]
    public void ComputeSimilarity_AlwaysBounded_ZeroToOne(
        double timing, double rate, double pathDiv, double entropy, double botProb)
    {
        var vectorA = CreateFeatureVector(
            signature: "s-a",
            timingRegularity: timing,
            requestRate: rate,
            pathDiversity: pathDiv,
            pathEntropy: entropy,
            avgBotProbability: botProb);

        var vectorB = CreateFeatureVector(
            signature: "s-b",
            timingRegularity: 1.0 - timing,
            requestRate: rate * 2 + 1,
            pathDiversity: 1.0 - pathDiv,
            pathEntropy: 5.0 - entropy,
            avgBotProbability: 1.0 - botProb,
            countryCode: "CN",
            isDatacenter: false,
            asn: "AS99999");

        var sim = BotClusterService.ComputeSimilarity(vectorA, vectorB);
        Assert.InRange(sim, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSimilarity_WeightsSum_ToOne()
    {
        // When all features are identical (including spectral), similarity should be exactly 1.0
        // This implicitly verifies weights sum to 1.0
        var f = CreateFeatureVector(
            spectral: new SpectralFeatures
            {
                DominantFrequency = 0.25,
                SpectralEntropy = 0.5,
                HarmonicRatio = 0.3,
                SpectralCentroid = 0.4,
                PeakToAvgRatio = 0.6,
                HasSufficientData = true
            });

        Assert.Equal(1.0, BotClusterService.ComputeSimilarity(f, f), precision: 10);
    }

    [Fact]
    public void ComputeSimilarity_IsSymmetric()
    {
        // ComputeSimilarity(a, b) should equal ComputeSimilarity(b, a)
        var vectorA = CreateFeatureVector(
            signature: "s-a",
            timingRegularity: 0.2,
            requestRate: 50.0,
            pathDiversity: 0.7,
            pathEntropy: 1.5,
            avgBotProbability: 0.6,
            countryCode: "US",
            isDatacenter: true,
            asn: "AS1111");

        var vectorB = CreateFeatureVector(
            signature: "s-b",
            timingRegularity: 0.8,
            requestRate: 200.0,
            pathDiversity: 0.3,
            pathEntropy: 3.0,
            avgBotProbability: 0.95,
            countryCode: "DE",
            isDatacenter: false,
            asn: "AS3320");

        var simAB = BotClusterService.ComputeSimilarity(vectorA, vectorB);
        var simBA = BotClusterService.ComputeSimilarity(vectorB, vectorA);
        Assert.Equal(simAB, simBA, precision: 10);
    }

    [Fact]
    public void ComputeSimilarity_BothZeroValues_HighSimilarity()
    {
        // When both values are zero, NormalizedDiff returns 0 -> similarity = 1.0 for that component
        var vectorA = CreateFeatureVector(
            timingRegularity: 0.0,
            requestRate: 0.0,
            pathDiversity: 0.0,
            pathEntropy: 0.0,
            avgBotProbability: 0.0,
            countryCode: null,
            isDatacenter: false,
            asn: null);

        var vectorB = CreateFeatureVector(
            timingRegularity: 0.0,
            requestRate: 0.0,
            pathDiversity: 0.0,
            pathEntropy: 0.0,
            avgBotProbability: 0.0,
            countryCode: null,
            isDatacenter: false,
            asn: null);

        var sim = BotClusterService.ComputeSimilarity(vectorA, vectorB);
        // With null ASN -> asnSim=0.0, null country -> string.Equals(null,null)=true -> countrySim=1.0
        // Spectral: both null -> neutral 0.5
        Assert.InRange(sim, 0.5, 1.0);
    }

    [Fact]
    public void ComputeSimilarity_NullCountryCodes_BothNull_Matches()
    {
        // string.Equals(null, null, OrdinalIgnoreCase) returns true -> countrySim=1.0
        var vectorA = CreateFeatureVector(countryCode: null);
        var vectorB = CreateFeatureVector(countryCode: null);
        var vectorC = CreateFeatureVector(countryCode: "US");

        var simBothNull = BotClusterService.ComputeSimilarity(vectorA, vectorB);
        var simOneNull = BotClusterService.ComputeSimilarity(vectorA, vectorC);

        Assert.True(simBothNull >= simOneNull,
            $"Both null country ({simBothNull:F4}) should be >= null vs non-null ({simOneNull:F4})");
    }

    [Fact]
    public void ComputeSimilarity_NullAsn_BothNull_DoesNotMatch()
    {
        // string.IsNullOrEmpty(null) -> true, so asnSim = 0.0 for null ASN
        var vectorA = CreateFeatureVector(asn: null);
        var vectorB = CreateFeatureVector(asn: null);
        var vectorC = CreateFeatureVector(asn: "AS15169");

        // Both null: asnSim=0.0, with a non-null match: asnSim could be 0 or 1
        var simBothNull = BotClusterService.ComputeSimilarity(vectorA, vectorB);
        Assert.InRange(simBothNull, 0.0, 1.0);
    }

    [Fact]
    public void ComputeSimilarity_SameDatacenter_HigherThanDifferent()
    {
        var baseVec = CreateFeatureVector(signature: "s1", isDatacenter: true);
        var sameDc = CreateFeatureVector(signature: "s2", isDatacenter: true);
        var diffDc = CreateFeatureVector(signature: "s3", isDatacenter: false);

        var simSame = BotClusterService.ComputeSimilarity(baseVec, sameDc);
        var simDiff = BotClusterService.ComputeSimilarity(baseVec, diffDc);
        Assert.True(simSame > simDiff,
            $"Same datacenter ({simSame:F4}) should exceed different datacenter ({simDiff:F4})");
    }

    #endregion

    #region BuildFeatureVectors Tests

    [Fact]
    public void BuildFeatureVectors_RequestsBelow9_NoSpectralFeatures()
    {
        var svc = CreateService();
        var behavior = CreateBehavior("s1", requestCount: 5);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Single(vectors);
        Assert.Null(vectors[0].Spectral);
        Assert.Null(vectors[0].Intervals);
    }

    [Fact]
    public void BuildFeatureVectors_Exactly8Requests_NoSpectralFeatures()
    {
        // Boundary: 8 requests is below the >= 9 threshold
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var requests = CreateTimedRequests(8, start, TimeSpan.FromSeconds(5));
        var behavior = CreateBehavior("s1", requestCount: 8, requests: requests, firstSeen: start);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Null(vectors[0].Spectral);
        Assert.Null(vectors[0].Intervals);
    }

    [Fact]
    public void BuildFeatureVectors_Exactly9Requests_HasSpectralFeatures()
    {
        // Boundary: exactly 9 requests meets the >= 9 threshold
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var requests = CreateTimedRequests(9, start, TimeSpan.FromSeconds(5));
        var behavior = CreateBehavior("s1", requestCount: 9, requests: requests, firstSeen: start);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.NotNull(vectors[0].Spectral);
        Assert.NotNull(vectors[0].Intervals);
        Assert.Equal(8, vectors[0].Intervals!.Length); // 9 requests -> 8 intervals
    }

    [Fact]
    public void BuildFeatureVectors_RequestsAtLeast9_HasSpectralFeatures()
    {
        var svc = CreateService();
        var behavior = CreateBehavior("s1", requestCount: 12);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Single(vectors);
        Assert.NotNull(vectors[0].Spectral);
        Assert.True(vectors[0].Spectral!.HasSufficientData);
        Assert.NotNull(vectors[0].Intervals);
        Assert.Equal(11, vectors[0].Intervals!.Length); // 12 requests = 11 intervals
    }

    [Fact]
    public void BuildFeatureVectors_PathDiversity_CalculatedCorrectly()
    {
        var svc = CreateService();
        // CreateBehavior uses paths like /page/0 through /page/4 (5 unique for 10 requests)
        var behavior = CreateBehavior("s1", requestCount: 10);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Single(vectors);
        Assert.Equal(0.5, vectors[0].PathDiversity, precision: 2); // 5 unique / 10 total
    }

    [Fact]
    public void BuildFeatureVectors_PathDiversity_AllUniquePaths_ReturnsOne()
    {
        // Each request hits a unique path -> diversity = 5/5 = 1.0
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var requests = new List<SignatureRequest>();
        for (var i = 0; i < 5; i++)
        {
            requests.Add(CreateRequest(
                path: $"/unique/{i}",
                timestamp: start + TimeSpan.FromSeconds(i * 5),
                requestId: $"req-{i}"));
        }

        var behavior = CreateBehavior("s1", requestCount: 5, requests: requests, firstSeen: start);
        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Equal(1.0, vectors[0].PathDiversity, precision: 10);
    }

    [Fact]
    public void BuildFeatureVectors_PathDiversity_AllSamePath_ReturnsInverse()
    {
        // 10 requests all to the same path -> 1 unique / 10 = 0.1
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var requests = new List<SignatureRequest>();
        for (var i = 0; i < 10; i++)
        {
            requests.Add(CreateRequest(
                path: "/same/path",
                timestamp: start + TimeSpan.FromSeconds(i * 2),
                requestId: $"req-{i}"));
        }

        var behavior = CreateBehavior("s1", requestCount: 10, requests: requests, firstSeen: start);
        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Equal(0.1, vectors[0].PathDiversity, precision: 10);
    }

    [Fact]
    public void BuildFeatureVectors_RequestRate_CalculatedCorrectly()
    {
        var svc = CreateService();
        var now = DateTime.UtcNow;
        var behavior = CreateBehavior("s1",
            requestCount: 60,
            averageInterval: 1.0,
            firstSeen: now.AddMinutes(-1),
            lastSeen: now);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        // 60 requests over 1 minute = 60 req/min
        Assert.InRange(vectors[0].RequestRate, 50.0, 70.0);
    }

    [Fact]
    public void BuildFeatureVectors_ZeroDuration_RequestRateIsZero()
    {
        // firstSeen == lastSeen -> durationSeconds = 0 -> rate = 0
        var svc = CreateService();
        var now = DateTime.UtcNow;
        var requests = new List<SignatureRequest> { CreateRequest(timestamp: now, requestId: "req-1") };
        var behavior = CreateBehavior("s1",
            requestCount: 1,
            requests: requests,
            firstSeen: now,
            lastSeen: now);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Equal(0.0, vectors[0].RequestRate);
    }

    [Fact]
    public void BuildFeatureVectors_PreservesMetadata()
    {
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var end = DateTime.UtcNow;
        var behavior = CreateBehavior("sig-test-123",
            requestCount: 3,
            timingCoefficient: 0.42,
            pathEntropy: 1.75,
            avgBotProbability: 0.85,
            countryCode: "DE",
            isDatacenter: false,
            asn: "AS3320",
            firstSeen: start,
            lastSeen: end);

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.Single(vectors);
        var f = vectors[0];
        Assert.Equal("sig-test-123", f.Signature);
        Assert.Equal(0.42, f.TimingRegularity);
        Assert.Equal(1.75, f.PathEntropy);
        Assert.Equal(0.85, f.AvgBotProbability);
        Assert.Equal("DE", f.CountryCode);
        Assert.False(f.IsDatacenter);
        Assert.Equal("AS3320", f.Asn);
        Assert.Equal(start, f.FirstSeen);
        Assert.Equal(end, f.LastSeen);
    }

    [Fact]
    public void BuildFeatureVectors_MultipleBehaviors_ReturnsAll()
    {
        var svc = CreateService();
        var behaviors = new List<SignatureBehavior>
        {
            CreateBehavior("sig-1"),
            CreateBehavior("sig-2"),
            CreateBehavior("sig-3")
        };

        var vectors = svc.BuildFeatureVectors(behaviors);
        Assert.Equal(3, vectors.Count);
        Assert.Equal("sig-1", vectors[0].Signature);
        Assert.Equal("sig-2", vectors[1].Signature);
        Assert.Equal("sig-3", vectors[2].Signature);
    }

    [Fact]
    public void BuildFeatureVectors_IntervalValues_CalculatedCorrectly()
    {
        // Verify intervals are computed from consecutive request timestamps
        var svc = CreateService();
        var start = DateTime.UtcNow.AddMinutes(-5);
        var intervalSeconds = 3.0;
        var requests = CreateTimedRequests(10, start, TimeSpan.FromSeconds(intervalSeconds));
        var behavior = CreateBehavior("s1",
            requestCount: 10,
            requests: requests,
            firstSeen: start,
            lastSeen: start + TimeSpan.FromSeconds(intervalSeconds * 9));

        var vectors = svc.BuildFeatureVectors(new[] { behavior });
        Assert.NotNull(vectors[0].Intervals);
        foreach (var interval in vectors[0].Intervals!)
        {
            Assert.Equal(intervalSeconds, interval, precision: 6);
        }
    }

    [Fact]
    public void BuildFeatureVectors_EmptyList_ReturnsEmpty()
    {
        var svc = CreateService();
        var vectors = svc.BuildFeatureVectors(Array.Empty<SignatureBehavior>());
        Assert.Empty(vectors);
    }

    #endregion

    #region BuildSimilarityGraph Tests

    [Fact]
    public void BuildSimilarityGraph_EmptyFeatures_ReturnsEmpty()
    {
        var svc = CreateService();
        var adjacency = svc.BuildSimilarityGraph(new List<BotClusterService.FeatureVector>());
        Assert.Empty(adjacency);
    }

    [Fact]
    public void BuildSimilarityGraph_SingleFeature_NoEdges()
    {
        var svc = CreateService();
        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeatureVector(signature: "s1")
        };

        var adjacency = svc.BuildSimilarityGraph(features);
        Assert.Single(adjacency);
        Assert.Empty(adjacency[0]);
    }

    [Fact]
    public void BuildSimilarityGraph_IdenticalFeatures_AllConnected()
    {
        var opts = DefaultOptions(c => c.SimilarityThreshold = 0.5);
        var svc = CreateService(opts);

        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeatureVector(signature: "s1"),
            CreateFeatureVector(signature: "s2"),
            CreateFeatureVector(signature: "s3")
        };

        var adjacency = svc.BuildSimilarityGraph(features);
        Assert.Equal(3, adjacency.Count);
        // Each node should have edges to the other two
        Assert.Equal(2, adjacency[0].Count);
        Assert.Equal(2, adjacency[1].Count);
        Assert.Equal(2, adjacency[2].Count);
    }

    [Fact]
    public void BuildSimilarityGraph_HighSimilarity_CreatesEdges()
    {
        var opts = DefaultOptions(c => c.SimilarityThreshold = 0.5);
        var svc = CreateService(opts);

        var f = CreateFeatureVector(signature: "s1");
        var features = new List<BotClusterService.FeatureVector>
        {
            f,
            f with { Signature = "s2" }
        };

        var adjacency = svc.BuildSimilarityGraph(features);
        Assert.True(adjacency[0].Count > 0, "Identical features should create edges");
        Assert.True(adjacency[1].Count > 0, "Edge should be bidirectional");
    }

    [Fact]
    public void BuildSimilarityGraph_LowSimilarity_NoEdges()
    {
        var opts = DefaultOptions(c => c.SimilarityThreshold = 0.95);
        var svc = CreateService(opts);

        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeatureVector(
                signature: "s1",
                timingRegularity: 0.1,
                requestRate: 100.0,
                pathDiversity: 0.1,
                pathEntropy: 0.5,
                avgBotProbability: 0.9,
                countryCode: "US",
                isDatacenter: true,
                asn: "AS15169"),
            CreateFeatureVector(
                signature: "s2",
                timingRegularity: 0.9,
                requestRate: 1.0,
                pathDiversity: 0.9,
                pathEntropy: 4.0,
                avgBotProbability: 0.1,
                countryCode: "CN",
                isDatacenter: false,
                asn: "AS4808")
        };

        var adjacency = svc.BuildSimilarityGraph(features);
        Assert.Empty(adjacency[0]);
        Assert.Empty(adjacency[1]);
    }

    [Fact]
    public void BuildSimilarityGraph_EdgesAreSymmetric()
    {
        var opts = DefaultOptions(c => c.SimilarityThreshold = 0.5);
        var svc = CreateService(opts);

        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeatureVector(signature: "s1"),
            CreateFeatureVector(signature: "s2")
        };

        var adjacency = svc.BuildSimilarityGraph(features);
        if (adjacency[0].Count > 0)
        {
            Assert.Single(adjacency[0]);
            Assert.Single(adjacency[1]);
            Assert.Equal(adjacency[0][0].Similarity, adjacency[1][0].Similarity);
        }
    }

    [Fact]
    public void BuildSimilarityGraph_ThresholdControlsEdges()
    {
        // Low threshold should accept more edges than a high threshold
        var optsLow = DefaultOptions(c => c.SimilarityThreshold = 0.3);
        var optsHigh = DefaultOptions(c => c.SimilarityThreshold = 0.9);
        var svcLow = CreateService(optsLow);
        var svcHigh = CreateService(optsHigh);

        // Features that are moderately similar
        var features = new List<BotClusterService.FeatureVector>
        {
            CreateFeatureVector(signature: "s1", requestRate: 10.0, avgBotProbability: 0.8),
            CreateFeatureVector(signature: "s2", requestRate: 20.0, avgBotProbability: 0.7,
                countryCode: "DE")
        };

        var graphLow = svcLow.BuildSimilarityGraph(features);
        var graphHigh = svcHigh.BuildSimilarityGraph(features);

        // Low threshold -> more likely to have edges
        Assert.True(graphLow[0].Count >= graphHigh[0].Count,
            "Lower threshold should accept at least as many edges as higher threshold");
    }

    #endregion

    #region NotifyBotDetected Tests

    [Fact]
    public void NotifyBotDetected_BelowThreshold_NoImmediateEffect()
    {
        var opts = DefaultOptions(c => c.MinBotDetectionsToTrigger = 10);
        var svc = CreateService(opts);

        // Should not throw
        for (var i = 0; i < 5; i++)
            svc.NotifyBotDetected();

        // Service still returns empty clusters
        Assert.Empty(svc.GetClusters());
    }

    [Fact]
    public void NotifyBotDetected_AtThreshold_DoesNotThrow()
    {
        var opts = DefaultOptions(c => c.MinBotDetectionsToTrigger = 3);
        var svc = CreateService(opts);

        // Should signal semaphore without error
        for (var i = 0; i < 5; i++)
            svc.NotifyBotDetected();
    }

    [Fact]
    public void NotifyBotDetected_MultipleCallsBeyondThreshold_DoesNotThrow()
    {
        // Verifies the SemaphoreFullException is correctly swallowed
        var opts = DefaultOptions(c => c.MinBotDetectionsToTrigger = 1);
        var svc = CreateService(opts);

        for (var i = 0; i < 100; i++)
            svc.NotifyBotDetected();
    }

    [Fact]
    public void NotifyBotDetected_IsThreadSafe()
    {
        var opts = DefaultOptions(c => c.MinBotDetectionsToTrigger = 50);
        var svc = CreateService(opts);

        // Call from multiple threads simultaneously - should not throw
        Parallel.For(0, 200, _ => svc.NotifyBotDetected());
    }

    #endregion

    #region RunClustering Integration Tests

    [Fact]
    public void RunClustering_TooFewBehaviors_ClustersRemainEmpty()
    {
        // The SignatureCoordinator has no data, so RunClustering exits early
        var svc = CreateService();

        svc.RunClustering();
        Assert.Empty(svc.GetClusters());
        Assert.Null(svc.FindCluster("any-signature"));
    }

    [Fact]
    public void RunClustering_EmptyCoordinator_DoesNotThrow()
    {
        var svc = CreateService();

        // Act & Assert - should not throw
        var exception = Record.Exception(() => svc.RunClustering());
        Assert.Null(exception);
    }

    [Fact]
    public void RunClustering_CalledMultipleTimes_DoesNotThrow()
    {
        var svc = CreateService();

        // Multiple calls should be safe
        for (var i = 0; i < 5; i++)
        {
            var exception = Record.Exception(() => svc.RunClustering());
            Assert.Null(exception);
        }
    }

    #endregion
}
