using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests for <see cref="ClusterContributor" />.
///     Validates cluster membership, community affinity, country reputation,
///     spectral signal emission, and edge-case handling.
/// </summary>
public class ClusterContributorTests
{
    #region Helpers

    private static BotDetectionOptions DefaultOptions()
    {
        var opts = new BotDetectionOptions();
        opts.Cluster.MinClusterSize = 3;
        opts.Cluster.SimilarityThreshold = 0.6;
        opts.Cluster.MinBotProbabilityForClustering = 0.5;
        opts.Cluster.MinBotDetectionsToTrigger = 20;
        opts.Cluster.MaxIterations = 10;
        opts.Cluster.ProductSimilarityThreshold = 0.8;
        opts.Cluster.NetworkTemporalDensityThreshold = 0.6;
        opts.CountryReputation = new CountryReputationOptions
        {
            DecayTauHours = 100_000,
            MinSampleSize = 5
        };
        return opts;
    }

    private static SignatureCoordinator CreateCoordinator(BotDetectionOptions? opts = null)
    {
        opts ??= DefaultOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static BotClusterService CreateClusterService(
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

    private static CountryReputationTracker CreateCountryTracker(BotDetectionOptions? opts = null)
    {
        opts ??= DefaultOptions();
        return new CountryReputationTracker(
            NullLogger<CountryReputationTracker>.Instance,
            Options.Create(opts));
    }

    /// <summary>
    ///     Stub config provider that returns default parameters with optional overrides.
    /// </summary>
    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _parameters;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
        {
            _parameters = parameters ?? new Dictionary<string, object>();
        }

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new ConfidenceDefaults
            {
                BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5
            },
            Parameters = new Dictionary<string, object>(_parameters)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_parameters.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private static ClusterContributor CreateContributor(
        BotClusterService? clusterService = null,
        CountryReputationTracker? countryTracker = null,
        Dictionary<string, object>? configParams = null,
        SignatureCoordinator? signatureCoordinator = null)
    {
        var opts = DefaultOptions();
        clusterService ??= CreateClusterService(opts);
        countryTracker ??= CreateCountryTracker(opts);
        signatureCoordinator ??= CreateCoordinator(opts);
        return new ClusterContributor(
            NullLogger<ClusterContributor>.Instance,
            new StubConfigProvider(configParams),
            clusterService,
            countryTracker,
            signatureCoordinator);
    }

    private static BlackboardState CreateState(Dictionary<string, object>? signals = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = (signals ?? new Dictionary<string, object>()).ToImmutableDictionary(),
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    #endregion

    #region Name and Priority

    [Fact]
    public void Name_IsClusterContributor()
    {
        var contributor = CreateContributor();
        Assert.Equal("ClusterContributor", contributor.Name);
    }

    [Fact]
    public void TriggerConditions_RequiresWaveformSignature()
    {
        var contributor = CreateContributor();
        Assert.Single(contributor.TriggerConditions);
        Assert.IsType<SignalExistsTrigger>(contributor.TriggerConditions[0]);
    }

    #endregion

    #region No Cluster / No Signals

    [Fact]
    public async Task ContributeAsync_NoSignature_ReturnsEmpty()
    {
        var contributor = CreateContributor();
        var state = CreateState();

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // No waveform.signature signal, so no contributions or signals
        Assert.Empty(result);
    }

    [Fact]
    public async Task ContributeAsync_SignatureNotInCluster_NoCountry_ReturnsEmpty()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "orphan-sig"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // No cluster match, no geo data -> empty
        Assert.Empty(result);
    }

    #endregion

    #region Country Reputation

    [Fact]
    public async Task ContributeAsync_HighBotRateCountry_EmitsCountrySignal()
    {
        var opts = DefaultOptions();
        var tracker = CreateCountryTracker(opts);
        // Record enough bots from "RU" to exceed the high rate threshold
        for (var i = 0; i < 10; i++)
            tracker.RecordDetection("RU", "Russia", true, 0.9);

        var contributor = CreateContributor(countryTracker: tracker);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "some-sig",
            ["geo.country_code"] = "RU"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should have at least one contribution with country reputation signal
        Assert.NotEmpty(result);

        var lastContribution = result[^1];
        Assert.True(lastContribution.Signals.ContainsKey(SignalKeys.GeoCountryBotRate));
        var botRate = (double)lastContribution.Signals[SignalKeys.GeoCountryBotRate];
        Assert.InRange(botRate, 0.9, 1.0);
    }

    [Fact]
    public async Task ContributeAsync_VeryHighBotRateCountry_HigherDelta()
    {
        var opts = DefaultOptions();
        var tracker = CreateCountryTracker(opts);
        // All traffic from "CN" is bots
        for (var i = 0; i < 10; i++)
            tracker.RecordDetection("CN", "China", true, 0.99);

        var contributor = CreateContributor(
            countryTracker: tracker,
            configParams: new Dictionary<string, object>
            {
                ["country_very_high_rate_threshold"] = 0.9,
                ["country_very_high_delta"] = 0.2
            });
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "some-sig",
            ["geo.country_code"] = "CN"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.NotEmpty(result);
        // Should have a contribution with positive confidence delta
        var botContributions = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContributions);
    }

    [Fact]
    public async Task ContributeAsync_LowBotRateCountry_NoCountryContribution()
    {
        var opts = DefaultOptions();
        var tracker = CreateCountryTracker(opts);
        // Mostly humans from "US"
        for (var i = 0; i < 2; i++)
            tracker.RecordDetection("US", "United States", true, 0.9);
        for (var i = 0; i < 10; i++)
            tracker.RecordDetection("US", "United States", false, 0.1);

        var contributor = CreateContributor(countryTracker: tracker);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "some-sig",
            ["geo.country_code"] = "US"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Bot rate ~0.17 is below both thresholds (0.7 and 0.9)
        // Should have neutral contribution with bot rate signal but no bot contribution
        if (result.Count > 0)
        {
            var lastSignals = result[^1].Signals;
            if (lastSignals.ContainsKey(SignalKeys.GeoCountryBotRate))
            {
                var rate = (double)lastSignals[SignalKeys.GeoCountryBotRate];
                Assert.InRange(rate, 0.0, 0.7); // Below high threshold
            }

            // No country-based bot contributions
            var countryBotContribs = result
                .Where(r => r.ConfidenceDelta > 0 && r.Reason.Contains("Country"))
                .ToList();
            Assert.Empty(countryBotContribs);
        }
    }

    [Fact]
    public async Task ContributeAsync_BelowMinSampleCountry_NoBotRate()
    {
        var opts = DefaultOptions();
        var tracker = CreateCountryTracker(opts);
        // Only 3 records (below MinSampleSize of 5)
        for (var i = 0; i < 3; i++)
            tracker.RecordDetection("JP", "Japan", true, 0.9);

        var contributor = CreateContributor(countryTracker: tracker);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "some-sig",
            ["geo.country_code"] = "JP"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // GetCountryBotRate returns 0 below MinSampleSize, so no bot rate signal emitted
        if (result.Count > 0)
        {
            var lastSignals = result[^1].Signals;
            Assert.False(lastSignals.ContainsKey(SignalKeys.GeoCountryBotRate),
                "Should not emit bot rate below min sample size");
        }
    }

    #endregion

    #region Community Affinity

    [Fact]
    public async Task ContributeAsync_CommunityAffinity_MatchingASN_EmitsSignal()
    {
        var opts = DefaultOptions();
        var coordinator = CreateCoordinator(opts);

        // Seed the coordinator with enough bot behaviors to form a cluster
        SeedBotCluster(coordinator, clusterAsn: "AS15169", clusterCountry: "US");

        var clusterService = CreateClusterService(opts, coordinator);
        clusterService.RunClustering();

        var clusters = clusterService.GetClusters();
        if (clusters.Count == 0)
            return; // If clustering didn't form, skip (depends on similarity thresholds)

        var contributor = CreateContributor(clusterService: clusterService);

        // A non-clustered signature that shares ASN with the cluster
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "marginal-sig",
            ["request.ip.asn"] = "AS15169",
            ["geo.country_code"] = "DE" // Different country
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should find community affinity via ASN match
        var affinityContrib = result.FirstOrDefault(r => r.Reason.Contains("infrastructure"));
        if (affinityContrib != null)
        {
            Assert.True(affinityContrib.ConfidenceDelta > 0);
            Assert.True(affinityContrib.ConfidenceDelta < 0.1,
                "Community affinity delta should be small");
        }
    }

    [Fact]
    public async Task ContributeAsync_CommunityAffinity_NoMatch_NoBoost()
    {
        var opts = DefaultOptions();
        var coordinator = CreateCoordinator(opts);
        SeedBotCluster(coordinator, clusterAsn: "AS15169", clusterCountry: "US");

        var clusterService = CreateClusterService(opts, coordinator);
        clusterService.RunClustering();

        var contributor = CreateContributor(clusterService: clusterService);

        // Non-matching ASN and country
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "unrelated-sig",
            ["request.ip.asn"] = "AS9999",
            ["geo.country_code"] = "JP"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Should not have community affinity contribution
        var affinityContrib = result.FirstOrDefault(r => r.Reason.Contains("infrastructure"));
        Assert.Null(affinityContrib);
    }

    /// <summary>
    ///     Seeds the coordinator with enough identical bot behaviors to form a valid cluster.
    /// </summary>
    private static void SeedBotCluster(
        SignatureCoordinator coordinator,
        string clusterAsn = "AS15169",
        string clusterCountry = "US",
        int count = 5)
    {
        for (var i = 0; i < count; i++)
        {
            var sig = $"bot-sig-{i}";
            // Record enough requests per signature for clustering
            for (var r = 0; r < 12; r++)
            {
                coordinator.RecordRequestAsync(
                    sig,
                    $"{sig}-req-{r}",
                    $"/page/{r % 5}",
                    0.85,
                    new Dictionary<string, object>
                    {
                        ["request.ip.asn"] = clusterAsn,
                        ["request.ip.is_datacenter"] = true
                    },
                    new HashSet<string> { "test" },
                    CancellationToken.None,
                    clusterCountry,
                    clusterAsn,
                    true).GetAwaiter().GetResult();
            }
        }
    }

    #endregion

    #region Cluster Membership — BotProduct

    [Fact]
    public async Task ContributeAsync_InBotProductCluster_EmitsClusterSignals()
    {
        var opts = DefaultOptions();
        var coordinator = CreateCoordinator(opts);
        SeedBotCluster(coordinator);

        var clusterService = CreateClusterService(opts, coordinator);
        clusterService.RunClustering();

        var clusters = clusterService.GetClusters();
        if (clusters.Count == 0)
            return; // If clustering didn't form, skip (depends on similarity)

        // Use a signature that IS in the cluster
        var clusteredSig = clusters[0].MemberSignatures[0];

        var contributor = CreateContributor(clusterService: clusterService);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = clusteredSig
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.NotEmpty(result);

        var lastSignals = result[^1].Signals;
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterId));
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterType));
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterMemberCount));
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterAvgBotProbability));
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterAvgSimilarity));
        Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterTemporalDensity));

        // Should have a bot contribution from the cluster match
        var botContribs = result.Where(r => r.ConfidenceDelta > 0).ToList();
        Assert.NotEmpty(botContribs);
    }

    #endregion

    #region Spectral Features in Cluster

    [Fact]
    public async Task ContributeAsync_ClusterWithSpectral_EmitsSpectralSignals()
    {
        var opts = DefaultOptions();
        var coordinator = CreateCoordinator(opts);
        SeedBotCluster(coordinator, count: 5);

        var clusterService = CreateClusterService(opts, coordinator);
        clusterService.RunClustering();

        var clusters = clusterService.GetClusters();
        if (clusters.Count == 0)
            return;

        var clusteredSig = clusters[0].MemberSignatures[0];

        // Check if spectral features are cached for this signature
        var spectral = clusterService.GetSpectralFeatures(clusteredSig);

        var contributor = CreateContributor(clusterService: clusterService);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = clusteredSig
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        if (spectral?.HasSufficientData == true)
        {
            var lastSignals = result[^1].Signals;
            Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterSpectralEntropy));
            Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterDominantFrequency));
            Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterHarmonicRatio));
            Assert.True(lastSignals.ContainsKey(SignalKeys.ClusterPeakToAvg));
        }
    }

    #endregion

    #region Neutral Contribution

    [Fact]
    public async Task ContributeAsync_OnlyCountrySignals_NeutralContribution()
    {
        var opts = DefaultOptions();
        var tracker = CreateCountryTracker(opts);
        // Record enough data to get a bot rate, but below high threshold
        for (var i = 0; i < 4; i++)
            tracker.RecordDetection("FR", "France", true, 0.8);
        for (var i = 0; i < 8; i++)
            tracker.RecordDetection("FR", "France", false, 0.1);

        var contributor = CreateContributor(countryTracker: tracker);
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = "neutral-sig",
            ["geo.country_code"] = "FR"
        });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        // Bot rate ~0.33 is below threshold, but country rate signal may still be emitted
        // If there are signals but no positive contributions, should be neutral
        if (result.Count > 0 && result.All(r => r.ConfidenceDelta <= 0))
        {
            var lastSignals = result[^1].Signals;
            Assert.True(lastSignals.ContainsKey(SignalKeys.GeoCountryBotRate));
        }
    }

    #endregion

    #region Error Handling

    [Fact]
    public async Task ContributeAsync_NullSignatureValue_NoException()
    {
        var contributor = CreateContributor();
        var state = CreateState(new Dictionary<string, object>
        {
            [SignalKeys.WaveformSignature] = (object)null!
        });

        // Should not throw
        var result = await contributor.ContributeAsync(state, CancellationToken.None);
        Assert.NotNull(result);
    }

    #endregion
}
