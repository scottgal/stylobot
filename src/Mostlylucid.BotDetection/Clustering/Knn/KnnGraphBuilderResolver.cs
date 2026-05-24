using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Clustering.Knn;

/// <summary>
///     Composes <see cref="SqliteVecKnnGraphBuilder"/> with a brute-force
///     fallback. Tries vec first; on failure (extension missing, below
///     threshold size, or any vec0 error), falls back to
///     <see cref="BruteForceKnnGraphBuilder"/>. Same graceful-fallback
///     pattern as <c>SqliteVecIdentityAnchorIndex</c>.
/// </summary>
public sealed class KnnGraphBuilderResolver : IKnnGraphBuilder
{
    private readonly IKnnGraphBuilder _preferred;
    private readonly IKnnGraphBuilder _fallback;
    private readonly ILogger<KnnGraphBuilderResolver> _logger;
    private bool _preferredFailedOnce;

    public KnnGraphBuilderResolver(
        SqliteVecKnnGraphBuilder vecBuilder,
        BruteForceKnnGraphBuilder bruteForceBuilder,
        ILogger<KnnGraphBuilderResolver> logger)
        : this((IKnnGraphBuilder)vecBuilder, bruteForceBuilder, logger) { }

    /// <summary>
    ///     Test-friendly overload: any two <see cref="IKnnGraphBuilder"/>
    ///     implementations. Production wiring goes through the concrete-
    ///     typed constructor.
    /// </summary>
    public KnnGraphBuilderResolver(
        IKnnGraphBuilder preferred,
        IKnnGraphBuilder fallback,
        ILogger<KnnGraphBuilderResolver> logger)
    {
        _preferred = preferred;
        _fallback = fallback;
        _logger = logger;
    }

    public Dictionary<int, List<(int Neighbor, double Weight)>> Build(
        IReadOnlyList<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
        int k,
        double minSimilarity,
        CancellationToken ct = default)
    {
        if (!_preferredFailedOnce)
        {
            try
            {
                return _preferred.Build(features, similarity, k, minSimilarity, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Expected fallback signal: not enough features OR vec0
                // unavailable. Log once at INFO so the operator sees the
                // path choice; subsequent cycles silently use the fallback.
                _logger.LogInformation(
                    "vec0 KNN builder unavailable ({Reason}); switching to brute-force for cluster graphs.",
                    ex.Message);
                _preferredFailedOnce = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "vec0 KNN builder threw; falling back to brute-force.");
                _preferredFailedOnce = true;
            }
        }
        return _fallback.Build(features, similarity, k, minSimilarity, ct);
    }
}
