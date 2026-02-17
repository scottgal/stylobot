namespace Mostlylucid.StyloSpam.Outgoing.Services;

public sealed record UserSendStats(
    string TenantId,
    string UserId,
    int LastHourCount,
    DateTimeOffset UpdatedAtUtc);

public interface IUserSendHistoryStore
{
    int RecordAndCountLastHour(string tenantId, string userId, DateTimeOffset atUtc);
    UserSendStats GetStats(string tenantId, string userId);
}
