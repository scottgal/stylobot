using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Core.Services;

public sealed class EmailScoringEngine
{
    private readonly IReadOnlyList<IEmailScoreContributor> _contributors;
    private readonly EmailScoringOptions _options;

    public EmailScoringEngine(
        IEnumerable<IEmailScoreContributor> contributors,
        IOptions<EmailScoringOptions> options)
    {
        _contributors = contributors.ToList();
        _options = options.Value;
    }

    public async Task<EmailScoreResult> EvaluateAsync(
        EmailEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var allContributions = new List<ScoreContribution>();

        foreach (var contributor in _contributors)
        {
            var contributions = await contributor.EvaluateAsync(envelope, cancellationToken);
            if (contributions.Count > 0)
            {
                allContributions.AddRange(contributions);
            }
        }

        var totalWeight = allContributions.Sum(c => Math.Abs(c.Weight));
        var weightedDelta = allContributions.Sum(c => c.WeightedDelta);
        var normalizedDelta = totalWeight <= 0.0001 ? 0 : weightedDelta / totalWeight;

        var spamScore = Math.Clamp(_options.BaselineScore + normalizedDelta, 0.0, 1.0);
        var confidence = Math.Clamp(
            totalWeight <= 0.0001
                ? 0.1
                : Math.Abs(weightedDelta) / (totalWeight + 0.25),
            0.05,
            0.99);

        var verdict = ScoreToVerdict(spamScore);

        var reasons = allContributions
            .OrderByDescending(c => c.WeightedDelta)
            .ThenByDescending(c => c.ScoreDelta)
            .Take(8)
            .Select(c => $"[{c.Contributor}] {c.Reason}")
            .ToList();

        return new EmailScoreResult(
            envelope.Mode,
            spamScore,
            confidence,
            verdict,
            reasons,
            allContributions,
            DateTimeOffset.UtcNow);
    }

    private SpamVerdict ScoreToVerdict(double score)
    {
        if (score >= _options.BlockThreshold) return SpamVerdict.Block;
        if (score >= _options.QuarantineThreshold) return SpamVerdict.Quarantine;
        if (score >= _options.WarnThreshold) return SpamVerdict.Warn;
        if (score >= _options.TagThreshold) return SpamVerdict.Tag;
        return SpamVerdict.Allow;
    }
}
