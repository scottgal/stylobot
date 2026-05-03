using Mostlylucid.StyloSpam.Core.Models;
using Mostlylucid.StyloSpam.Core.Services;

namespace Mostlylucid.StyloSpam.Core.Contributors;

public sealed class AttachmentRiskContributor : IEmailScoreContributor
{
    private static readonly HashSet<string> HighRiskExtensions =
    [
        ".exe", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jar", ".hta", ".com"
    ];

    public string Name => "AttachmentRisk";

    public Task<IReadOnlyList<ScoreContribution>> EvaluateAsync(EmailEnvelope envelope, CancellationToken cancellationToken = default)
    {
        if (envelope.Attachments.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([]);
        }

        var riskyCount = envelope.Attachments.Count(a =>
        {
            var ext = Path.GetExtension(a.FileName);
            return !string.IsNullOrWhiteSpace(ext) && HighRiskExtensions.Contains(ext.ToLowerInvariant());
        });

        if (riskyCount == 0)
        {
            return Task.FromResult<IReadOnlyList<ScoreContribution>>([
                new ScoreContribution(Name, "Attachment", 0.08, 1.0,
                    $"{envelope.Attachments.Count} attachment(s) present")
            ]);
        }

        var delta = Math.Min(0.25 + (riskyCount * 0.20), 0.85);
        return Task.FromResult<IReadOnlyList<ScoreContribution>>([
            new ScoreContribution(Name, "Attachment", delta, 1.4,
                $"Detected {riskyCount} high-risk attachment(s)")
        ]);
    }
}
