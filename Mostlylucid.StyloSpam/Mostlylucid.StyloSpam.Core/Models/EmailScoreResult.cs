namespace Mostlylucid.StyloSpam.Core.Models;

public sealed record EmailScoreResult(
    EmailFlowMode Mode,
    double SpamScore,
    double Confidence,
    SpamVerdict Verdict,
    IReadOnlyList<string> TopReasons,
    IReadOnlyList<ScoreContribution> Contributions,
    DateTimeOffset EvaluatedAtUtc);
