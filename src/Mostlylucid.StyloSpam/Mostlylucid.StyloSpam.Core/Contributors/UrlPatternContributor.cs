using System.Text.RegularExpressions;
using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed partial class UrlPatternContributor : IEmailScoreContributor
{
    public string Name => "UrlPattern";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var body = string.Join("\n", envelope.EnumerateBodies());
        if (string.IsNullOrWhiteSpace(body))
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var urls = UrlRegex().Matches(body);
        if (urls.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var risky = 0;
        foreach (Match match in urls)
        {
            var url = match.Value;
            if (url.Contains("@") || url.Contains("xn--", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("bit.ly", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("tinyurl", StringComparison.OrdinalIgnoreCase))
            {
                risky++;
            }
        }

        var total = urls.Count;
        var riskRatio = total == 0 ? 0 : (double)risky / total;
        var delta = Math.Min(0.08 + (total * 0.015) + (riskRatio * 0.30), 0.65);

        return Task.FromResult<IReadOnlyList<ScoreContribution>>(
        [
            new ScoreContribution(
                Name,
                "Links",
                delta,
                1.1,
                $"Detected {total} URL(s), {risky} with obfuscation/high-risk patterns")
        ]);
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
