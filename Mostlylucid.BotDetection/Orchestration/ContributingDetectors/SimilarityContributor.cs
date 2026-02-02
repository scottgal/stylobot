using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Similarity search contributor.
///     Uses HNSW approximate nearest neighbor search to find behaviorally similar
///     past signatures and adjust bot confidence accordingly.
///     Runs after the Heuristic contributor (Priority 50) to leverage its feature extraction,
///     but before HeuristicLate (Priority 100) so its signals influence final scoring.
/// </summary>
public class SimilarityContributor : ContributingDetectorBase
{
    private const float BotSimilarityThreshold = 0.85f;
    private const float HumanSimilarityThreshold = 0.85f;
    private const double BotBoostConfidence = 0.3;
    private const double HumanReduceConfidence = -0.2;

    private readonly FeatureVectorizer _vectorizer;
    private readonly ISignatureSimilaritySearch _search;
    private readonly ILogger<SimilarityContributor> _logger;

    public SimilarityContributor(
        FeatureVectorizer vectorizer,
        ISignatureSimilaritySearch search,
        ILogger<SimilarityContributor> logger)
    {
        _vectorizer = vectorizer;
        _search = search;
        _logger = logger;
    }

    public override string Name => "Similarity";
    public override int Priority => 60; // After Heuristic (50), before HeuristicLate (100)

    // Requires heuristic prediction to have completed (ensures features are available)
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new SignalExistsTrigger(SignalKeys.HeuristicPrediction)
    };

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var signals = ImmutableDictionary.CreateBuilder<string, object>();

        try
        {
            // Skip if index is empty
            if (_search.Count == 0)
            {
                signals.Add(SignalKeys.SimilarityMatchCount, 0);
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Similarity",
                    ConfidenceDelta = 0.0,
                    Weight = 1.0,
                    Reason = "Similarity search skipped (empty index)",
                    Signals = signals.ToImmutable()
                });
            }

            // Build aggregated evidence to extract features
            var evidence = BuildAggregatedEvidence(state);
            var features = HeuristicFeatureExtractor.ExtractFeatures(state.HttpContext, evidence);
            var vector = _vectorizer.Vectorize(features);

            // Search for similar signatures
            var similar = await _search.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.80f);

            signals.Add(SignalKeys.SimilarityMatchCount, similar.Count);

            if (similar.Count > 0)
            {
                var topResult = similar[0];
                var topSimilarity = 1.0f - topResult.Distance;
                signals.Add(SignalKeys.SimilarityTopScore, topSimilarity);
                signals.Add(SignalKeys.SimilarityKnownBot, topResult.WasBot);

                // Count how many similar signatures are bots vs humans
                var botMatches = similar.Count(s => s.WasBot);
                var humanMatches = similar.Count - botMatches;

                if (botMatches > humanMatches && topSimilarity >= BotSimilarityThreshold)
                {
                    // Most similar signatures were bots - boost bot confidence
                    var boost = BotBoostConfidence * topSimilarity;
                    contributions.Add(DetectionContribution.Bot(
                        Name, "Similarity", boost,
                        $"Similar to {botMatches} known bot signature(s) (top similarity: {topSimilarity:F2})",
                        weight: 1.4,
                        botType: BotType.Scraper.ToString()));
                }
                else if (humanMatches > botMatches && topSimilarity >= HumanSimilarityThreshold)
                {
                    // Most similar signatures were human - reduce bot confidence
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "Similarity",
                        ConfidenceDelta = HumanReduceConfidence * topSimilarity,
                        Weight = 1.3,
                        Reason =
                            $"Similar to {humanMatches} known human signature(s) (top similarity: {topSimilarity:F2})",
                        Signals = signals.ToImmutable()
                    });
                }
                else
                {
                    // Mixed results or below threshold - neutral
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "Similarity",
                        ConfidenceDelta = 0.0,
                        Weight = 1.0,
                        Reason =
                            $"Found {similar.Count} similar signatures (mixed or below threshold, top: {topSimilarity:F2})",
                        Signals = signals.ToImmutable()
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in similarity search");
            signals.TryAdd("similarity.error", ex.Message);
        }

        // Always emit at least one contribution with signals
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Similarity",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Similarity search complete (no matches above threshold)",
                Signals = signals.ToImmutable()
            });
        }
        else
        {
            // Ensure last contribution carries all signals
            var last = contributions[^1];
            contributions[^1] = last with { Signals = signals.ToImmutable() };
        }

        return contributions;
    }

    /// <summary>
    ///     Build a temporary AggregatedEvidence from current blackboard state.
    ///     Uses a DetectionLedger so the Contributions property is correctly populated.
    /// </summary>
    private static AggregatedEvidence BuildAggregatedEvidence(BlackboardState state)
    {
        // Aggregate signals from blackboard + contributions
        var signals = new Dictionary<string, object>();
        foreach (var signal in state.Signals) signals[signal.Key] = signal.Value;
        foreach (var contrib in state.Contributions)
        foreach (var signal in contrib.Signals)
            signals[signal.Key] = signal.Value;

        var detectorNames = state.Contributions.Select(c => c.DetectorName).ToHashSet();

        // Build a DetectionLedger with all contributions
        var tempLedger = new DetectionLedger("temp-similarity");
        foreach (var contrib in state.Contributions)
            tempLedger.AddContribution(contrib);

        return new AggregatedEvidence
        {
            Ledger = tempLedger,
            BotProbability = state.CurrentRiskScore,
            Confidence = 0.5,
            RiskBand = RiskBand.Medium,
            Signals = signals,
            TotalProcessingTimeMs = state.Elapsed.TotalMilliseconds,
            CategoryBreakdown = tempLedger.CategoryBreakdown,
            ContributingDetectors = detectorNames,
            FailedDetectors = state.FailedDetectors
        };
    }
}
