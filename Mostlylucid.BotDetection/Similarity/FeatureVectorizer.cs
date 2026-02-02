using System.Numerics.Tensors;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Converts the dynamic feature dictionary from HeuristicFeatureExtractor
///     into a fixed-length float[] vector suitable for HNSW similarity search.
///     Uses a stable schema of feature names mapped to vector indices.
/// </summary>
public sealed class FeatureVectorizer
{
    /// <summary>
    ///     Current schema version. Increment when the schema changes
    ///     to invalidate saved vectors that are no longer compatible.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     Fixed vector dimension (padded from actual feature count).
    /// </summary>
    public const int VectorDimension = 64;

    /// <summary>
    ///     Ordered feature names that define the vector schema.
    ///     Each feature name maps to a specific index in the output vector.
    ///     Missing features default to 0.0f.
    /// </summary>
    private static readonly string[] Schema =
    {
        // req:* (7 features) - basic request metadata
        "req:ua_length",
        "req:path_length",
        "req:query_count",
        "req:content_length",
        "req:is_https",
        "req:header_count",
        "req:cookie_count",

        // hdr:* (6 features) - header presence
        "hdr:accept-language",
        "hdr:accept",
        "hdr:referer",
        "hdr:origin",
        "hdr:x-requested-with",
        "hdr:connection-close",

        // ua:* (18 features) - user agent flags
        "ua:contains_bot",
        "ua:contains_spider",
        "ua:contains_crawler",
        "ua:contains_scraper",
        "ua:headless",
        "ua:phantomjs",
        "ua:selenium",
        "ua:chrome",
        "ua:firefox",
        "ua:safari",
        "ua:edge",
        "ua:curl",
        "ua:wget",
        "ua:python",
        "ua:scrapy",
        "ua:requests",
        "ua:httpx",
        "ua:aiohttp",

        // accept:* (3 features) - accept header analysis
        "accept:wildcard",
        "accept:html",
        "accept:json",

        // stat:* (11 features) - aggregated statistics
        "stat:detector_count",
        "stat:detector_flagged",
        "stat:detector_max",
        "stat:detector_avg",
        "stat:detector_variance",
        "stat:category_count",
        "stat:category_max",
        "stat:category_avg",
        "stat:contribution_count",
        "stat:signal_count",
        "stat:failed_count",

        // result:* (4 features) - final results
        "result:bot_probability",
        "result:confidence",
        "result:early_exit",
        "result:risk_band"

        // Remaining indices (50-63) are padding zeros for future expansion
    };

    /// <summary>
    ///     Lookup table mapping feature names to vector indices.
    /// </summary>
    private static readonly Dictionary<string, int> SchemaIndex;

    static FeatureVectorizer()
    {
        SchemaIndex = new Dictionary<string, int>(Schema.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Schema.Length; i++)
            SchemaIndex[Schema[i]] = i;
    }

    /// <summary>
    ///     Convert a feature dictionary to a fixed-length float vector.
    ///     Missing features default to 0.0f. The vector is L2-normalized.
    /// </summary>
    /// <param name="features">Feature dictionary from HeuristicFeatureExtractor</param>
    /// <returns>Fixed-length normalized float vector</returns>
    public float[] Vectorize(Dictionary<string, float> features)
    {
        var vector = new float[VectorDimension];

        foreach (var (name, value) in features)
        {
            if (SchemaIndex.TryGetValue(name, out var index))
                vector[index] = value;
        }

        // L2-normalize via SIMD-accelerated TensorPrimitives for cosine distance
        var norm = TensorPrimitives.Norm(vector.AsSpan());
        if (norm > 0f)
            TensorPrimitives.Divide(vector.AsSpan(), norm, vector.AsSpan());

        return vector;
    }
}
