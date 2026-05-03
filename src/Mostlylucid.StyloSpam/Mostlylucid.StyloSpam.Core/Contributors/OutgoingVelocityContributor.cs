using Microsoft.Extensions.Options;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class OutgoingVelocityContributor : IEmailScoreContributor
{
    private readonly EmailScoringOptions _options;

    public OutgoingVelocityContributor(IOptions<EmailScoringOptions> options)
    {
        _options = options.Value;
    }

    public string Name => "OutgoingVelocity";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Mode != EmailFlowMode.Outgoing)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        if (!envelope.Metadata.TryGetValue("outgoing.user_messages_last_hour", out var value))
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        if (!TryToInt(value, out var count))
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        if (count >= _options.OutgoingVelocityQuarantineThresholdPerHour)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([
                new ScoreContribution(Name, "Behavior", 0.70, 1.5,
                    $"User send velocity is very high ({count}/hr)")
            ]);
        }

        if (count >= _options.OutgoingVelocityWarnThresholdPerHour)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([
                new ScoreContribution(Name, "Behavior", 0.30, 1.2,
                    $"User send velocity is elevated ({count}/hr)")
            ]);
        }

        return Task.FromResult<IReadOnlyList<ScoreContribution>>([
            new ScoreContribution(Name, "Behavior", -0.05, 1.0,
                $"User send velocity is normal ({count}/hr)")
        ]);
    }

    private static bool TryToInt(object value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l when l <= int.MaxValue:
                result = (int)l;
                return true;
            case string s when int.TryParse(s, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
