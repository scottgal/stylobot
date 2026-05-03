namespace Mostlylucid.BotDetection.Analysis;

/// <summary>
///     Fixed radar dimension set. Every DetectionRadarShape MUST contain all 16 dimensions
///     (0.0 if not applicable). This ensures all shapes are comparable and all vectors
///     are the same length for similarity search.
/// </summary>
public static class RadarDimensions
{
    public const int Count = 16;

    public const string UaAnomaly = "ua_anomaly";
    public const string HeaderAnomaly = "header_anomaly";
    public const string IpReputation = "ip_reputation";
    public const string Behavioral = "behavioral";
    public const string AdvancedBehavioral = "advanced_behavioral";
    public const string CacheBehavior = "cache_behavior";
    public const string SecurityTool = "security_tool";
    public const string ClientFingerprint = "client_fingerprint";
    public const string VersionAge = "version_age";
    public const string Inconsistency = "inconsistency";
    public const string ReputationMatch = "reputation_match";
    public const string AiClassification = "ai_classification";
    public const string ClusterSignal = "cluster_signal";
    public const string CountryReputation = "country_reputation";
    public const string RatePattern = "rate_pattern";
    public const string PayloadSignature = "payload_signature";

    /// <summary>All dimension names in fixed order. Index position = vector position.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        UaAnomaly, HeaderAnomaly, IpReputation, Behavioral,
        AdvancedBehavioral, CacheBehavior, SecurityTool, ClientFingerprint,
        VersionAge, Inconsistency, ReputationMatch, AiClassification,
        ClusterSignal, CountryReputation, RatePattern, PayloadSignature
    };

    /// <summary>Map from old 8-axis names to new 16-dimension names for migration.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyMapping = new Dictionary<string, string>
    {
        ["Navigation"] = Behavioral,
        ["API Usage"] = RatePattern,
        ["Asset Loading"] = CacheBehavior,
        ["Timing Regularity"] = AdvancedBehavioral,
        ["Request Rate"] = RatePattern,
        ["Path Diversity"] = Behavioral,
        ["Fingerprint"] = ClientFingerprint,
        ["Timing Anomaly"] = AdvancedBehavioral
    };

    /// <summary>Create an empty dimension dictionary with all dimensions set to 0.</summary>
    public static Dictionary<string, double> CreateEmpty()
    {
        var dims = new Dictionary<string, double>(Count);
        foreach (var name in All) dims[name] = 0.0;
        return dims;
    }

    /// <summary>Convert a dimension dictionary to a fixed-length float vector.</summary>
    public static float[] ToVector(IReadOnlyDictionary<string, double> dimensions)
    {
        var vector = new float[Count];
        for (var i = 0; i < All.Count; i++)
            vector[i] = dimensions.TryGetValue(All[i], out var val) ? (float)val : 0f;
        return vector;
    }

    /// <summary>Project a 129-dim session vector to a RadarDimensions dictionary.</summary>
    public static Dictionary<string, double>? FromSessionVector(float[] sessionVector)
    {
        var projected = SearchProjection.Project(sessionVector);
        if (projected is null) return null;

        var dims = new Dictionary<string, double>(Count);
        for (var i = 0; i < All.Count; i++)
            dims[All[i]] = projected[i];
        return dims;
    }
}
