using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class SpamPhraseContributor : IEmailScoreContributor
{
    private readonly EmailScoringOptions _options;

    public SpamPhraseContributor(IOptions<EmailScoringOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "SpamPhrase";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var body = string.Join("\n", envelope.EnumerateBodies()).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(body))
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var matches = _options.SpamPhrases
            .Where(p => body.Contains(p, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var boost = Math.Min(0.15 + (matches.Count * 0.07), 0.55);
        return Task.FromResult<IReadOnlyList<ScoreContribution>>(
        [
            new ScoreContribution(
                Name,
                "Content",
                boost,
                1.2,
                $"Detected {matches.Count} spam-like phrase(s): {string.Join(", ", matches.Take(4))}")
        ]);
    }
}
