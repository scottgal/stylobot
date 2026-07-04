using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     RankerAtom (per Taxonomy.md) that queries the signature-similarity
///     HNSW index for behaviourally-similar past signatures and adjusts bot
///     confidence based on their prior classification. Priority 60 -- after
///     HeuristicAtom (50) so its feature vector reflects the early
///     prediction; before HeuristicLateAtom (100) so its signals feed into
///     the final pass.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>SimilarityContributor</c>. Reads prior contributions from the
///         sink via <see cref="SinkEvidenceReader"/> -- no ledger-access
///         contract needed -- because the ephemeral orchestrator publishes
///         them as sink signals.
///     </para>
///     <para>
///         Trigger: <see cref="SignalKeys.HeuristicPrediction"/> present on
///         the sink (early heuristic has completed and produced features).
///     </para>
/// </remarks>
public sealed class SimilarityAtom : DetectorAtomBase
{
    private readonly FeatureVectorizer _vectorizer;
    private readonly ISignatureSimilaritySearch _search;
    private readonly ILogger<SimilarityAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public SimilarityAtom(
        FeatureVectorizer vectorizer,
        ISignatureSimilaritySearch search,
        ILogger<SimilarityAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "Similarity", category: "Similarity")
    {
        _vectorizer = vectorizer;
        _search = search;
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 60;
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.HeuristicPrediction };

    private float BotSimilarityThreshold => (float)_configProvider.GetParameter(Name, "bot_similarity_threshold", 0.85);
    private float HumanSimilarityThreshold => (float)_configProvider.GetParameter(Name, "human_similarity_threshold", 0.85);
    private double BotBoostConfidence => _configProvider.GetParameter(Name, "bot_boost_confidence", 0.3);
    private double HumanReduceConfidence => _configProvider.GetParameter(Name, "human_reduce_confidence", -0.2);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        sink.Raise("similarity.ran", sessionId);

        var contributions = new List<DetectionContribution>();

        try
        {
            if (_search.Count == 0)
            {
                sink.Raise($"{SignalKeys.SimilarityMatchCount}:0", sessionId);
                return Single(DetectionContribution.Info(Name, Category, "No prior visitor signatures to compare against yet"));
            }

            // Rebuild AggregatedEvidence from the sink's contribution signals
            // so HeuristicFeatureExtractor's cross-signal feature builder sees
            // the same shape it would under the blackboard path.
            var evidence = SinkEvidenceReader.BuildEvidence(sink, context, "temp-similarity");
            var features = HeuristicFeatureExtractor.ExtractFeatures(context, evidence);
            var vector = _vectorizer.Vectorize(features);

            var ua = context.Request.Headers.UserAgent.ToString();
            var path = context.Request.Path.ToString();
            var embeddingContext = !string.IsNullOrEmpty(ua) ? $"UA:{ua} | Path:{path}" : null;

            var similar = await _search.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.80f, embeddingContext)
                .ConfigureAwait(false);

            sink.Raise($"{SignalKeys.SimilarityMatchCount}:{similar.Count}", sessionId);

            if (similar.Count > 0)
            {
                var topResult = similar[0];
                var topSimilarity = 1.0f - topResult.Distance;
                sink.Raise($"{SignalKeys.SimilarityTopScore}:{topSimilarity.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
                sink.Raise($"{SignalKeys.SimilarityKnownBot}:{(topResult.WasBot ? "true" : "false")}", sessionId);

                var botMatches = similar.Count(s => s.WasBot);
                var humanMatches = similar.Count - botMatches;

                if (botMatches > humanMatches && topSimilarity >= BotSimilarityThreshold)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = BotBoostConfidence * topSimilarity,
                        Weight = _configProvider.GetParameter(Name, "bot_match_weight", 1.4),
                        Reason = $"Resembles {botMatches} previously identified bot(s) ({topSimilarity:P0} match)",
                        BotType = BotType.Scraper.ToString()
                    });
                }
                else if (humanMatches > botMatches && topSimilarity >= HumanSimilarityThreshold)
                {
                    contributions.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = Category,
                        ConfidenceDelta = HumanReduceConfidence * topSimilarity,
                        Weight = _configProvider.GetParameter(Name, "human_match_weight", 1.3),
                        Reason = $"Resembles {humanMatches} previously verified human visitor(s) ({topSimilarity:P0} match)"
                    });
                }
                else
                {
                    contributions.Add(DetectionContribution.Info(Name, Category,
                        $"Found {similar.Count} similar past visitors but results are inconclusive"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in similarity search");
        }

        if (contributions.Count == 0)
            contributions.Add(DetectionContribution.Info(Name, Category, "No closely matching past visitors found"));

        return contributions;
    }
}
