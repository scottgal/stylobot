using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Models;

public sealed record DetectResponse
{
    public required VerdictDto Verdict { get; init; }
    public required IReadOnlyList<ReasonDto> Reasons { get; init; }
    public required IReadOnlyDictionary<string, object> Signals { get; init; }
    public required MetaDto Meta { get; init; }

    public static DetectResponse FromEvidence(AggregatedEvidence evidence)
    {
        var recommendedAction = evidence.RiskBand switch
        {
            RiskBand.High or RiskBand.VeryHigh => RecommendedAction.Block,
            RiskBand.Medium => RecommendedAction.Challenge,
            RiskBand.Elevated => RecommendedAction.Throttle,
            _ => RecommendedAction.Allow
        };

        return new DetectResponse
        {
            Verdict = new VerdictDto
            {
                IsBot = evidence.BotProbability >= 0.7,
                BotProbability = Math.Round(evidence.BotProbability, 4),
                Confidence = Math.Round(evidence.Confidence, 4),
                BotType = evidence.PrimaryBotType?.ToString(),
                BotName = evidence.PrimaryBotName,
                RiskBand = evidence.RiskBand.ToString(),
                RecommendedAction = recommendedAction.ToString(),
                ThreatScore = Math.Round(evidence.ThreatScore, 4),
                ThreatBand = evidence.ThreatBand.ToString()
            },
            Reasons = evidence.Contributions
                .Where(c => Math.Abs(c.ConfidenceDelta) > 0.01)
                .Select(c => new ReasonDto
                {
                    Detector = c.DetectorName,
                    Detail = c.Reason ?? c.DetectorName,
                    Impact = Math.Round(c.ConfidenceDelta, 4)
                })
                .ToList(),
            Signals = evidence.Signals,
            Meta = new MetaDto
            {
                ProcessingTimeMs = Math.Round(evidence.TotalProcessingTimeMs, 2),
                DetectorsRun = evidence.ContributingDetectors.Count,
                PolicyName = evidence.PolicyName,
                AiRan = evidence.AiRan
            }
        };
    }
}

public sealed record VerdictDto
{
    public required bool IsBot { get; init; }
    public required double BotProbability { get; init; }
    public required double Confidence { get; init; }
    public string? BotType { get; init; }
    public string? BotName { get; init; }
    public required string RiskBand { get; init; }
    public required string RecommendedAction { get; init; }
    public required double ThreatScore { get; init; }
    public required string ThreatBand { get; init; }
}

public sealed record ReasonDto
{
    public required string Detector { get; init; }
    public required string Detail { get; init; }
    public required double Impact { get; init; }
}

public sealed record MetaDto
{
    public required double ProcessingTimeMs { get; init; }
    public required int DetectorsRun { get; init; }
    public string? PolicyName { get; init; }
    public required bool AiRan { get; init; }
    public string? RequestId { get; init; }
}
