namespace Mostlylucid.StyloSpam.Core.Models;

public sealed record ScoreContribution(
    string Contributor,
    string Category,
    double ScoreDelta,
    double Weight,
    string Reason)
{
    public double WeightedDelta => ScoreDelta * Weight;
}
