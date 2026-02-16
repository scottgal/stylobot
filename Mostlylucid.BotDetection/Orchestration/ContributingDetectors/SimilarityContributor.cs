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

        try
        {
            // Skip if index is empty
            if (_search.Count == 0)
            {
                state.WriteSignal(SignalKeys.SimilarityMatchCount, 0);
                return Single(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Similarity",
                    ConfidenceDelta = 0.0,
                    Weight = 1.0,
                    Reason = "No prior visitor signatures to compare against yet"
                });
            }

            // Build aggregated evidence to extract features
            var evidence = BuildAggregatedEvidence(state);
            var features = HeuristicFeatureExtractor.ExtractFeatures(state.HttpContext, evidence);
            var vector = _vectorizer.Vectorize(features);

            // Build embedding context from request data for semantic search
            var ua = state.HttpContext.Request.Headers.UserAgent.ToString();
            var path = state.HttpContext.Request.Path.ToString();
            var embeddingContext = !string.IsNullOrEmpty(ua)
                ? $"UA:{ua} | Path:{path}"
                : null;

            // Search for similar signatures (heuristic + semantic when available)
            var similar = await _search.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.80f, embeddingContext);

            state.WriteSignal(SignalKeys.SimilarityMatchCount, similar.Count);

            if (similar.Count > 0)
            {
                var topResult = similar[0];
                var topSimilarity = 1.0f - topResult.Distance;
                state.WriteSignal(SignalKeys.SimilarityTopScore, topSimilarity);
                state.WriteSignal(SignalKeys.SimilarityKnownBot, topResult.WasBot);

                // Count how many similar signatures are bots vs humans
                var botMatches = similar.Count(s => s.WasBot);
                var humanMatches = similar.Count - botMatches;

                if (botMatches > humanMatches && topSimilarity >= BotSimilarityThreshold)
                {
                    // Most similar signatures were bots - boost bot confidence
                    var boost = BotBoostConfidence * topSimilarity;
                    contributions.Add(DetectionContribution.Bot(
                        Name, "Similarity", boost,
                        $"Resembles {botMatches} previously identified bot(s) ({topSimilarity:P0} match)",
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
                            $"Resembles {humanMatches} previously verified human visitor(s) ({topSimilarity:P0} match)"
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
                            $"Found {similar.Count} similar past visitors but results are inconclusive"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in similarity search");
            state.WriteSignal("similarity.error", ex.Message);
        }

        // Always emit at least one contribution
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Similarity",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "No closely matching past visitors found"
            });
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
