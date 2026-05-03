using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Clustering;

/// <summary>
///     Computes adaptive similarity weights based on feature diagnosticity.
///     Replaces hardcoded weights in ComputeSimilarity() with data-driven weights
///     that reflect how much each feature discriminates between signatures.
///
///     For continuous features: uses coefficient of variation (high CV = good discriminator).
///     For categorical features: uses Shannon entropy (high entropy = good discriminator).
///
///     Recalculated every clustering cycle (30s) so weights adapt to traffic mix.
///     Default weights come from cluster.detector.yaml parameters (weight_* keys).
/// </summary>
public sealed class AdaptiveSimilarityWeighter
{
    private const double WeightFloor = 0.02;
    private const double WeightCeiling = 0.20;
    private const string DetectorName = "ClusterContributor";

    private readonly ILogger<AdaptiveSimilarityWeighter> _logger;
    private readonly IDetectorConfigProvider? _configProvider;

    // Previous weights for change detection
    private Dictionary<string, double>? _previousWeights;

    public AdaptiveSimilarityWeighter(
        ILogger<AdaptiveSimilarityWeighter> logger,
        IDetectorConfigProvider? configProvider = null)
    {
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <summary>
    ///     Compute adaptive weights for all features in the current population.
    ///     Returns feature name → weight mapping (sums to 1.0).
    /// </summary>
    internal Dictionary<string, double> ComputeWeights(List<BotClusterService.FeatureVector> features)
    {
        if (features.Count < 3)
            return GetDefaultWeights();

        var diagnosticity = new Dictionary<string, double>();

        // Continuous features: coefficient of variation
        diagnosticity["timing"] = ComputeCV(features, f => f.TimingRegularity);
        diagnosticity["rate"] = ComputeCV(features, f => f.RequestRate);
        diagnosticity["pathDiv"] = ComputeCV(features, f => f.PathDiversity);
        diagnosticity["entropy"] = ComputeCV(features, f => f.PathEntropy);
        diagnosticity["botProb"] = ComputeCV(features, f => f.AvgBotProbability);

        // Geo proximity (continuous when we have lat/lon, categorical fallback)
        diagnosticity["geo"] = ComputeCategoricalEntropy(features, f => f.CountryCode ?? "?");

        // Categorical features: Shannon entropy
        diagnosticity["datacenter"] = ComputeCategoricalEntropy(features,
            f => f.IsDatacenter ? "dc" : "res");
        diagnosticity["asn"] = ComputeCategoricalEntropy(features, f => f.Asn ?? "?");

        // Spectral features (continuous)
        diagnosticity["spectralEntropy"] = ComputeCV(features,
            f => f.Spectral?.SpectralEntropy ?? 0.5);
        diagnosticity["harmonic"] = ComputeCV(features,
            f => f.Spectral?.HarmonicRatio ?? 0.5);
        diagnosticity["peakToAvg"] = ComputeCV(features,
            f => f.Spectral?.PeakToAvgRatio ?? 0.5);
        diagnosticity["dominantFreq"] = ComputeCV(features,
            f => f.Spectral?.DominantFrequency ?? 0.5);

        // Markov drift features (continuous, if available)
        diagnosticity["selfDrift"] = ComputeCV(features, f => f.SelfDrift);
        diagnosticity["humanDrift"] = ComputeCV(features, f => f.HumanDrift);
        diagnosticity["loopScore"] = ComputeCV(features, f => f.LoopScore);
        diagnosticity["surprise"] = ComputeCV(features, f => f.SequenceSurprise);
        diagnosticity["novelty"] = ComputeCV(features, f => f.TransitionNovelty);
        diagnosticity["entropyDelta"] = ComputeCV(features,
            f => Math.Abs(f.EntropyDelta));

        // Claimed identity features
        diagnosticity["uaFamily"] = ComputeCategoricalEntropy(features, f => f.UaFamily ?? "?");
        diagnosticity["claimedId"] = ComputeCV(features, f => f.ClaimedIdentityScore);

        // Normalize to sum to 1.0 with floor and ceiling
        var weights = NormalizeWithBounds(diagnosticity);

        // Detect significant weight shifts
        DetectWeightShifts(weights);
        _previousWeights = weights;

        return weights;
    }

    /// <summary>
    ///     Get weight shifts since last cycle (for transition event logging).
    /// </summary>
    public IReadOnlyList<(string Feature, double OldWeight, double NewWeight)> GetRecentShifts()
    {
        // Populated by DetectWeightShifts
        return _recentShifts;
    }

    private List<(string Feature, double OldWeight, double NewWeight)> _recentShifts = [];

    private static double ComputeCV(List<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, double> selector)
    {
        if (features.Count < 2) return 0;

        var values = features.Select(selector).ToList();
        var mean = values.Average();
        if (Math.Abs(mean) < 1e-9) return 0;

        var variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        var stdDev = Math.Sqrt(variance);
        return stdDev / Math.Abs(mean); // CV
    }

    private static double ComputeCategoricalEntropy(List<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, string> selector)
    {
        if (features.Count < 2) return 0;

        var counts = features
            .Select(selector)
            .GroupBy(v => v)
            .ToDictionary(g => g.Key, g => (double)g.Count() / features.Count);

        var entropy = 0.0;
        foreach (var p in counts.Values)
            if (p > 0) entropy -= p * Math.Log2(p);

        // Normalize by max possible entropy (log2(n_categories))
        var maxEntropy = Math.Log2(counts.Count);
        return maxEntropy > 0 ? entropy / maxEntropy : 0;
    }

    private static Dictionary<string, double> NormalizeWithBounds(Dictionary<string, double> diagnosticity)
    {
        var total = diagnosticity.Values.Sum();
        if (total <= 0)
            return diagnosticity.ToDictionary(kvp => kvp.Key, _ => 1.0 / diagnosticity.Count);

        var weights = new Dictionary<string, double>();

        // First pass: normalize
        foreach (var (key, value) in diagnosticity)
            weights[key] = value / total;

        // Apply floor and ceiling, then renormalize
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var clamped = false;
            foreach (var key in weights.Keys.ToList())
            {
                if (weights[key] < WeightFloor)
                {
                    weights[key] = WeightFloor;
                    clamped = true;
                }
                else if (weights[key] > WeightCeiling)
                {
                    weights[key] = WeightCeiling;
                    clamped = true;
                }
            }

            if (!clamped) break;

            // Renormalize
            total = weights.Values.Sum();
            foreach (var key in weights.Keys.ToList())
                weights[key] /= total;
        }

        return weights;
    }

    private void DetectWeightShifts(Dictionary<string, double> newWeights)
    {
        _recentShifts = [];

        if (_previousWeights == null) return;

        foreach (var (feature, newWeight) in newWeights)
        {
            if (_previousWeights.TryGetValue(feature, out var oldWeight))
            {
                var delta = Math.Abs(newWeight - oldWeight);
                if (delta > 0.03) // 3% shift is significant
                {
                    _recentShifts.Add((feature, oldWeight, newWeight));
                    _logger.LogInformation(
                        "Adaptive weight shift: {Feature} {OldWeight:P1} → {NewWeight:P1} (Δ{Delta:P1})",
                        feature, oldWeight, newWeight, delta);
                }
            }
        }
    }

    /// <summary>
    ///     Returns similarity weights read from cluster.detector.yaml (weight_* parameters).
    ///     Used as fallback when insufficient traffic data for adaptive computation.
    ///     All values come from YAML config; no weights are hardcoded here.
    /// </summary>
    public Dictionary<string, double> GetDefaultWeights()
    {
        T W<T>(string key, T fallback) => _configProvider != null
            ? _configProvider.GetParameter(DetectorName, key, fallback)
            : fallback;

        // The canonical values for these are in cluster.detector.yaml under defaults.parameters.
        // The fallback values here are only used if the config system cannot load the manifest
        // (e.g., embedded resource missing). They must match the YAML values.
        return new Dictionary<string, double>
        {
            ["timing"]          = W("weight_timing",          0.07),
            ["rate"]            = W("weight_rate",            0.06),
            ["pathDiv"]         = W("weight_path_div",        0.05),
            ["entropy"]         = W("weight_entropy",         0.05),
            ["botProb"]         = W("weight_bot_prob",        0.07),
            ["geo"]             = W("weight_geo",             0.07),
            ["datacenter"]      = W("weight_datacenter",      0.05),
            ["asn"]             = W("weight_asn",             0.05),
            ["spectralEntropy"] = W("weight_spectral_entropy",0.05),
            ["harmonic"]        = W("weight_harmonic",        0.04),
            ["peakToAvg"]       = W("weight_peak_to_avg",     0.05),
            ["dominantFreq"]    = W("weight_dominant_freq",   0.03),
            ["selfDrift"]       = W("weight_self_drift",      0.05),
            ["humanDrift"]      = W("weight_human_drift",     0.05),
            ["loopScore"]       = W("weight_loop_score",      0.04),
            ["surprise"]        = W("weight_surprise",        0.04),
            ["novelty"]         = W("weight_novelty",         0.04),
            ["entropyDelta"]    = W("weight_entropy_delta",   0.04),
            ["uaFamily"]        = W("weight_ua_family",       0.05),
            ["claimedId"]       = W("weight_claimed_id",      0.05),
        };
    }
}
