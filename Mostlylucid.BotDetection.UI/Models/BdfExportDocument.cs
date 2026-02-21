using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     BDF (Bot Detection Format) v2 document for exporting and replaying detection data.
///     Backwards-compatible with v1 fields at root level; v2 adds structured sections.
/// </summary>
public sealed record BdfExportDocument
{
    // ── v1 fields (k6 bdf-load-test.js compatibility) ──

    public string Version { get; init; } = "2.0";
    public required string ScenarioName { get; init; }
    public required string Scenario { get; init; }
    public double Confidence { get; init; }
    public bool IsBot { get; init; }

    // ── v2: signature identity ──

    public BdfSignatureInfo? Signature { get; init; }

    // ── v2: behavioral history (ring buffers from CachedVisitor) ──

    public BdfBehavioralHistory? BehavioralHistory { get; init; }

    // ── v2: per-detector breakdown ──

    public Dictionary<string, BdfDetectorContribution>? DetectorContributions { get; init; }

    // ── v2: all non-PII signals ──

    public Dictionary<string, object>? ImportantSignals { get; init; }

    // ── v1+v2: request sequence ──

    public List<BdfRequest> Requests { get; init; } = [];

    // ── v2: replay expectations ──

    public BdfExpectation? Expectation { get; init; }

    // ── v2: export metadata ──

    public BdfMetadata? Metadata { get; init; }
}

public sealed record BdfSignatureInfo
{
    public required string PrimarySignature { get; init; }
    public int Hits { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public string? RiskBand { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public string? Action { get; init; }
    public string? CountryCode { get; init; }
    public string? Narrative { get; init; }
}

public sealed record BdfBehavioralHistory
{
    public List<double> BotProbabilityHistory { get; init; } = [];
    public List<double> ConfidenceHistory { get; init; } = [];
    public List<double> ProcessingTimeHistory { get; init; } = [];
}

public sealed record BdfDetectorContribution
{
    public double ConfidenceDelta { get; init; }
    public double Contribution { get; init; }
    public string? Reason { get; init; }
    public double ExecutionTimeMs { get; init; }
}

public sealed record BdfRequest
{
    public double Timestamp { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public int ExpectedStatus { get; init; } = 200;
    public double DelayAfter { get; init; }
    public BdfExpectedDetection? ExpectedDetection { get; init; }
}

public sealed record BdfExpectedDetection
{
    public bool IsBot { get; init; }
    public double BotProbability { get; init; }
    public string? RiskBand { get; init; }
}

public sealed record BdfExpectation
{
    public string ExpectedClassification { get; init; } = "Human";
    public double MaxBotProbability { get; init; } = 0.3;
    public string MaxRiskBand { get; init; } = "Low";
}

public sealed record BdfMetadata
{
    public DateTime ExportedUtc { get; init; } = DateTime.UtcNow;
    public string PiiLevel { get; init; } = "none";
    public string? DetectorVersion { get; init; }
}
