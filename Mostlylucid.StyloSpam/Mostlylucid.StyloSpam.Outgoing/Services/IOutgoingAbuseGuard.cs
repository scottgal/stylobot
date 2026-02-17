using Mostlylucid.StyloSpam.Core.Models;

namespace Mostlylucid.StyloSpam.Outgoing.Services;

public sealed record AbuseGuardEvaluation(
    bool IsBlocked,
    string Reason,
    int CurrentStrikeCount,
    DateTimeOffset? BlockedUntilUtc);

public interface IOutgoingAbuseGuard
{
    AbuseGuardEvaluation EvaluateAndRecord(
        string tenantId,
        string userId,
        SpamVerdict verdict,
        DateTimeOffset atUtc);
}
