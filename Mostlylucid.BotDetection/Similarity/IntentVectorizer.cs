using System.Numerics.Tensors;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Converts session activity signals into a 32-dimensional intent vector
///     capturing WHAT the session is doing (not who it is).
///     Used by IntentContributor to query the intent HNSW index for known
///     threat patterns. Orthogonal to the identity-based FeatureVectorizer.
/// </summary>
public sealed class IntentVectorizer
{
    /// <summary>
    ///     Current schema version. Increment when the schema changes
    ///     to invalidate saved intent vectors that are no longer compatible.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     Fixed vector dimension for intent HNSW index.
    /// </summary>
    public const int VectorDimension = 32;

    /// <summary>
    ///     Ordered feature names that define the intent vector schema.
    ///     Each feature name maps to a specific index in the output vector.
    ///     Missing features default to 0.0f.
    /// </summary>
    private static readonly string[] Schema =
    {
        // Path category histogram (7 features) - what types of resources are being accessed
        "path:content",           // 0 - content/page requests
        "path:api",               // 1 - API endpoint requests
        "path:auth",              // 2 - authentication endpoints
        "path:admin",             // 3 - admin/management paths
        "path:static",            // 4 - static resources (css, js, images)
        "path:probe",             // 5 - known probe/scan paths
        "path:other",             // 6 - uncategorized paths

        // Path entropy features (2 features) - path diversity signals
        "path:entropy",           // 7 - Shannon entropy of path distribution
        "path:unique_ratio",      // 8 - unique paths / total requests

        // Response code distribution (5 features) - server response patterns
        "response:2xx_ratio",     // 9  - successful responses
        "response:3xx_ratio",     // 10 - redirects
        "response:4xx_ratio",     // 11 - client errors
        "response:5xx_ratio",     // 12 - server errors
        "response:404_ratio",     // 13 - specific 404 ratio (scanning signal)

        // Temporal features (4 features) - timing patterns
        "temporal:request_rate",  // 14 - requests per minute
        "temporal:burst_ratio",   // 15 - burst detection flag (0 or 1)
        "temporal:session_duration", // 16 - normalized session duration
        "temporal:interrequest_cv",  // 17 - coefficient of variation of inter-request timing

        // Attack features (4 features) - from HaxxorContributor
        "attack:has_injection",   // 18 - any injection detected (SQLi, XSS, CMDI, SSTI, SSRF)
        "attack:has_scanning",    // 19 - any scanning detected (path probe, config exposure, admin scan)
        "attack:category_count",  // 20 - number of distinct attack categories
        "attack:severity",        // 21 - severity score (low=0.25, medium=0.5, high=0.75, critical=1.0)

        // Transport shape (3 features) - protocol usage patterns
        "transport:content_ratio", // 22 - document/content request ratio
        "transport:stream_ratio",  // 23 - streaming (WS/SSE) request ratio
        "transport:api_ratio",     // 24 - API request ratio

        // Auth features (2 features) - authentication behavior
        "auth:failure_count",     // 25 - normalized auth failure count
        "auth:brute_force",       // 26 - brute force flag (0 or 1)

        // Behavioral features (2 features) - navigation patterns
        "behavior:path_repetition", // 27 - repeated path access ratio
        "behavior:depth_pattern",   // 28 - path depth variance (flat vs. deep crawl)

        // Response features (2 features) - probing indicators
        "response:honeypot_hits",   // 29 - normalized honeypot hit count
        "response:error_harvesting", // 30 - error harvesting flag (0 or 1)

        // Reserved (1 feature)
        "reserved:0"              // 31 - reserved for future expansion
    };

    /// <summary>
    ///     Lookup table mapping feature names to vector indices.
    /// </summary>
    private static readonly Dictionary<string, int> SchemaIndex;

    static IntentVectorizer()
    {
        SchemaIndex = new Dictionary<string, int>(Schema.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Schema.Length; i++)
            SchemaIndex[Schema[i]] = i;
    }

    /// <summary>
    ///     Convert an intent feature dictionary to a fixed-length float vector.
    ///     Missing features default to 0.0f. The vector is L2-normalized.
    /// </summary>
    /// <param name="features">Intent feature dictionary built from blackboard signals</param>
    /// <returns>Fixed-length normalized float vector (32 dimensions)</returns>
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

    /// <summary>
    ///     Get the ordered schema feature names (for diagnostics/debugging).
    /// </summary>
    public static IReadOnlyList<string> GetSchema() => Schema;
}
