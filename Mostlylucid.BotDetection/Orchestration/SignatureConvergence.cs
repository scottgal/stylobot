using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Reason a signature family was formed.
/// </summary>
public enum FamilyFormationReason
{
    TemporalProximity,
    BehavioralSimilarity,
    HighBotProbabilityCluster
}

/// <summary>
///     A non-destructive grouping of related signatures (same IP, rotating UAs).
///     Members are aggregated for clustering without destroying individual tracking.
///     Uses ConcurrentDictionary for thread-safe member access across background services and API reads.
/// </summary>
public sealed class SignatureFamily
{
    public required string FamilyId { get; init; }
    public required string CanonicalSignature { get; init; }
    public required ConcurrentDictionary<string, byte> MemberSignatures { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime LastEvaluatedUtc { get; set; }
    public FamilyFormationReason FormationReason { get; init; }
    public double MergeConfidence { get; init; }
    public int EvaluationCount { get; set; }

    /// <summary>
    ///     Creates a ConcurrentDictionary member set from a collection of signature strings.
    /// </summary>
    public static ConcurrentDictionary<string, byte> CreateMemberSet(IEnumerable<string> signatures)
    {
        var dict = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var sig in signatures)
            dict.TryAdd(sig, 0);
        return dict;
    }
}

/// <summary>
///     Candidate pair for merge evaluation.
/// </summary>
internal readonly record struct MergeCandidate(
    string SignatureA,
    string SignatureB,
    double TemporalScore,
    double BehavioralScore,
    double BotProbabilityScore,
    double TotalScore);

/// <summary>
///     Candidate for split evaluation.
/// </summary>
internal readonly record struct SplitCandidate(
    string FamilyId,
    string DivergentSignature,
    double MemberBotProbability,
    double FamilyAverageBotProbability,
    double Divergence);

/// <summary>
///     Configuration for signature convergence (merge/split).
/// </summary>
public class SignatureConvergenceOptions
{
    public bool Enabled { get; set; } = true;
    public int EvaluationIntervalSeconds { get; set; } = 15;
    public int TemporalProximityWindowSeconds { get; set; } = 300;
    public int MinSignaturesForMerge { get; set; } = 2;
    public double MergeScoreThreshold { get; set; } = 0.6;
    public double SplitDivergenceThreshold { get; set; } = 0.4;
    public int MinEvaluationsBeforeSplit { get; set; } = 3;
    public int MaxFamilies { get; set; } = 500;
    public double TemporalWeight { get; set; } = 0.3;
    public double BehavioralWeight { get; set; } = 0.3;
    public double BotProbabilityWeight { get; set; } = 0.4;
}
