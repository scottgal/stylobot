using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>Combined relational + vector filter for the investigation view.</summary>
public sealed record ShapeSearchFilter
{
    // Relational filters (WHERE clauses)
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? EndpointPath { get; init; }
    public string? HttpMethod { get; init; }
    public string? UserAgent { get; init; }
    public string? Country { get; init; }
    public string? BotName { get; init; }
    public bool? IsKnownBot { get; init; }
    public string? IpHmac { get; init; }         // paid only
    public string? UserIdentity { get; init; }    // paid only

    // Vector shape (for HNSW search)
    public float[]? TargetShape { get; init; }    // 16-dim target vector
    public float[]? DimensionWeights { get; init; } // 16-dim weights (0=ignore, 1=must match)
    public double FuzzThreshold { get; init; } = 0.2;

    // Pagination
    public int Limit { get; init; } = 50;
    public int Offset { get; init; } = 0;
    public string? Tab { get; init; }
}

/// <summary>A filter group for the accordion UI.</summary>
public sealed record FilterGroup
{
    public required string Id { get; init; }        // "fingerprint", "traffic", "behavioral"
    public required string Label { get; init; }     // "Fingerprint Signals"
    public required IReadOnlyList<FilterDimension> Dimensions { get; init; }
}

/// <summary>A single vector dimension within a filter group.</summary>
public sealed record FilterDimension
{
    public required string Name { get; init; }       // RadarDimensions axis name
    public required string Label { get; init; }      // "TLS Version", "Headless Score"
    public required int AxisIndex { get; init; }     // position in RadarDimensions.All
    public string InputType { get; init; } = "slider"; // "slider", "toggle", "dropdown"
    public double Value { get; init; }               // current value (0-1)
    public double Weight { get; init; } = 1.0;       // importance weight
    public double? Threshold { get; init; }          // for toggles: what "yes" means
    public IReadOnlyList<string>? Options { get; init; } // for dropdowns
}

/// <summary>A saved investigation preset (Leiden community, shipped, or user-created).</summary>
public sealed record InvestigationPreset
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Source { get; init; }     // "shipped", "leiden", "user"
    public required float[] TargetShape { get; init; }
    public double FuzzThreshold { get; init; } = 0.2;
    public string? RelationalFiltersJson { get; init; }
    public string? DimensionWeightsJson { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>View model for the shape-based investigation view.</summary>
public sealed class ShapeInvestigationViewModel
{
    public required ShapeSearchFilter Filter { get; init; }
    public required InvestigationResult Result { get; init; }
    public required string BasePath { get; init; }
    public required IReadOnlyList<FilterGroup> FilterGroups { get; init; }
    public required IReadOnlyList<InvestigationPreset> Presets { get; init; }
    public IReadOnlyList<string> AvailableTabs { get; init; } = [];
    public string ActiveTab => Filter.Tab ?? "detections";
    public bool HasShapeSearch { get; init; }  // whether pgvector is available
    public bool IsPaid { get; init; }

    private static readonly float[] EmptyShape = new float[RadarDimensions.Count];
    private static readonly float[] EmptyWeights = new float[RadarDimensions.Count];

    public float[] CurrentShape => Filter.TargetShape ?? EmptyShape;
    public float[] CurrentWeights => Filter.DimensionWeights ?? EmptyWeights;
}
