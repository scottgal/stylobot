using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Core.Services;

public interface IEmailScoreContributor
{
    string Name { get; }

    Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(
        EmailEnvelope envelope,
        CancellationToken cancellationToken = default);
}
