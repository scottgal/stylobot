using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class RecipientSpreadContributor : IEmailScoreContributor
{
    private readonly EmailScoringOptions _options;

    public RecipientSpreadContributor(IOptions<EmailScoringOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "RecipientSpread";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Mode != EmailFlowMode.Outgoing)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var recipients = envelope.TotalRecipientCount;
        if (recipients >= _options.ExtremeRecipientCountThreshold)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([
                new ScoreContribution(Name, "Behavior", 0.70, 1.5,
                    $"Large recipient blast detected ({recipients} recipients)")
            ]);
        }

        if (recipients >= _options.HighRecipientCountThreshold)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([
                new ScoreContribution(Name, "Behavior", 0.35, 1.3,
                    $"High recipient fan-out ({recipients} recipients)")
            ]);
        }

        return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
    }
}
