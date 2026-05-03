namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>Filter for the unified investigation view. Each entity type becomes a WHERE clause.</summary>
public sealed record InvestigationFilter
{
    public required string EntityType { get; init; }  // signature, country, path, ua_family, ip, fingerprint
    public required string EntityValue { get; init; }
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; } = 0;
    public string? Tab { get; init; }  // which tab to render (detections, signatures, endpoints, geo, fingerprints, signaltrace)
}

/// <summary>Aggregated result for the investigation view. Tabs pull from different fields.</summary>
public sealed record InvestigationResult
{
    public required InvestigationSummary Summary { get; init; }
    public IReadOnlyList<DashboardDetectionEvent> Detections { get; init; } = [];
    public IReadOnlyList<SignatureSummary> Signatures { get; init; } = [];
    public IReadOnlyList<EndpointStat> EndpointStats { get; init; } = [];
    public IReadOnlyList<CountryStat> CountryBreakdown { get; init; } = [];
    public int TotalCount { get; init; }

    /// <summary>
    ///     Ghost campaign matches for signatures found in this result set.
    ///     Each entry is a crystallized dormant campaign whose 129D behavioural centroid
    ///     is similar to at least one signature's root_vector in the result.
    ///     FOSS: populated from L1/L2 HNSW centroid entries (available after first nightly compaction).
    ///     Commercial: additionally includes cross-gateway ghost shapes persisted to PostgreSQL.
    ///     Empty when no compacted centroids have been created yet.
    /// </summary>
    public IReadOnlyList<GhostCampaignHit> GhostMatches { get; init; } = [];

    /// <summary>
    ///     Unknown-space pressure [0, 1].
    ///     Non-zero when the shape query matched nothing and the nearest radar-shape
    ///     neighbour in the HNSW index is far away -- the query sits in unexplored space.
    ///     HIGH PRESSURE is not inherently bad (rare but legitimate visitors can also
    ///     have unique radar shapes). Treat as a contributor signal, not a direct alarm.
    ///     FOSS: always 0 (void pressure requires radar-space pgvector HNSW from commercial).
    ///     Commercial: computed by PostgresShapeSearchStore against the 16D radar HNSW.
    /// </summary>
    public float VoidPressure { get; init; }
}

/// <summary>At-a-glance summary for the filtered segment.</summary>
public sealed record InvestigationSummary
{
    public long TotalDetections { get; init; }
    public DateTime? FirstSeen { get; init; }
    public DateTime? LastSeen { get; init; }
    public int HighRisk { get; init; }
    public int MediumRisk { get; init; }
    public int LowRisk { get; init; }
    public IReadOnlyList<string> TopReasons { get; init; } = [];
}

/// <summary>Distinct signature seen in the result set.</summary>
public sealed record SignatureSummary
{
    public required string PrimarySignature { get; init; }
    public int HitCount { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? RiskBand { get; init; }
    public string? UaFamily { get; init; }
    public bool IsKnownBot { get; init; }
    public DateTime LastSeen { get; init; }
    public string? ClientSideSignature { get; init; }

    /// <summary>
    ///     Mean frequency fingerprint (8D autocorrelation) from compacted sessions.
    ///     FOSS: populated from HNSW L1 metadata (FrequencyFingerprint on SessionVectorMetadata),
    ///     written during VectorCompactionService Phase 2.
    ///     Null for signatures with no compacted sessions (before first nightly compaction).
    ///     Displayed as a bar chart in the Fingerprints tab to show temporal rhythm.
    /// </summary>
    public float[]? FrequencyCentroid { get; init; }
}

/// <summary>
///     A ghost campaign match for the investigation view.
///     Produced when a signature's 129D root_vector is similar to a crystallized campaign centroid.
/// </summary>
public sealed record GhostCampaignHit
{
    /// <summary>The ghost campaign family ID.</summary>
    public required string FamilyId { get; init; }

    /// <summary>Human-readable label set by analysts (null if unlabelled).</summary>
    public string? Label { get; init; }

    /// <summary>Cosine similarity between the matched signature's root_vector and the ghost centroid.</summary>
    public float Similarity { get; init; }

    /// <summary>Which signature in the result set triggered this ghost match.</summary>
    public required string MatchedSignature { get; init; }

    /// <summary>Risk band of the ghost campaign.</summary>
    public string RiskBand { get; init; } = "High";

    /// <summary>How many signatures were compacted into this ghost.</summary>
    public int SignatureCount { get; init; }

    /// <summary>When this ghost was last active.</summary>
    public DateTime LastSeen { get; init; }
}

/// <summary>Endpoint stats grouped by method + path.</summary>
public sealed record EndpointStat
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public int Count { get; init; }
    public double AvgBotProbability { get; init; }
    public string? DominantRiskBand { get; init; }
}

/// <summary>Country breakdown within the result set.</summary>
public sealed record CountryStat
{
    public required string CountryCode { get; init; }
    public int Count { get; init; }
    public int BotCount { get; init; }
    public string? DominantRiskBand { get; init; }
}
