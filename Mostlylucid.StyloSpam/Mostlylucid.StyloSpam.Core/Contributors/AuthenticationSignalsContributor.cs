using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class AuthenticationSignalsContributor : IEmailScoreContributor
{
    public string Name => "AuthenticationSignals";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var headers = envelope.Headers;
        var results = new List<ScoreContribution>();

        headers.TryGetValue("Authentication-Results", out var authResults);
        var auth = authResults ?? string.Empty;

        var hasDkimSignature = headers.ContainsKey("DKIM-Signature");
        if (!hasDkimSignature)
        {
            results.Add(new ScoreContribution(Name, "Auth", 0.20, 1.3, "Missing DKIM signature"));
        }

        if (!string.IsNullOrEmpty(auth))
        {
            if (auth.Contains("spf=pass", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", -0.12, 1.0, "SPF passed"));
            }
            else if (auth.Contains("spf=fail", StringComparison.OrdinalIgnoreCase) ||
                     auth.Contains("spf=softfail", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", 0.22, 1.2, "SPF failed or soft-failed"));
            }

            if (auth.Contains("dkim=pass", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", -0.10, 1.0, "DKIM passed"));
            }
            else if (auth.Contains("dkim=fail", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", 0.18, 1.1, "DKIM failed"));
            }

            if (auth.Contains("dmarc=pass", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", -0.12, 1.0, "DMARC passed"));
            }
            else if (auth.Contains("dmarc=fail", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ScoreContribution(Name, "Auth", 0.24, 1.3, "DMARC failed"));
            }
        }

        return Task.FromResult<IReadOnlyList<ScoreContribution>>(results);
    }
}
