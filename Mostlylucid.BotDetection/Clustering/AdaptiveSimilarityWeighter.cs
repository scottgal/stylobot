using Microsoft.Extensions.Logging;
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
/// </summary>
public sealed class AdaptiveSimilarityWeighter
{
    private const double WeightFloor = 0.02;
    private const double WeightCeiling = 0.20;

    private readonly ILogger<AdaptiveSimilarityWeighter> _logger;

    // Previous weights for change detection
    private Dictionary<string, double>? _previousWeights;

    public AdaptiveSimilarityWeighter(ILogger<AdaptiveSimilarityWeighter> logger)
    {
        _logger = logger;
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
    ///     Fallback weights when insufficient data for adaptive computation.
    ///     Matches the original hardcoded weights from ComputeSimilarity().
    /// </summary>
    public static Dictionary<string, double> GetDefaultWeights() => new()
    {
        ["timing"] = 0.08,
        ["rate"] = 0.07,
        ["pathDiv"] = 0.05,
        ["entropy"] = 0.05,
        ["botProb"] = 0.08,
        ["geo"] = 0.08,
        ["datacenter"] = 0.05,
        ["asn"] = 0.06,
        ["spectralEntropy"] = 0.06,
        ["harmonic"] = 0.04,
        ["peakToAvg"] = 0.05,
        ["dominantFreq"] = 0.03,
        ["selfDrift"] = 0.06,
        ["humanDrift"] = 0.06,
        ["loopScore"] = 0.05,
        ["surprise"] = 0.05,
        ["novelty"] = 0.04,
        ["entropyDelta"] = 0.04
    };
}
