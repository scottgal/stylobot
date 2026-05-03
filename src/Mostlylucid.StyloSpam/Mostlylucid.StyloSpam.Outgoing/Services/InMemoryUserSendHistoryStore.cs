using System.Collections.Concurrent;

namespace Mostlylucid.StyloSpam.Outgoing.Services;

public sealed class InMemoryUserSendHistoryStore : IUserSendHistoryStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _events = new();

    public int RecordAndCountLastHour(string tenantId, string userId, DateTimeOffset atUtc)
    {
        var key = BuildKey(tenantId, userId);
        var queue = _events.GetOrAdd(key, _ => new ConcurrentQueue<DateTimeOffset>());

        queue.Enqueue(atUtc);
        Trim(queue, atUtc);

        return queue.Count;
    }

    public UserSendStats GetStats(string tenantId, string userId)
    {
        var key = BuildKey(tenantId, userId);
        if (!_events.TryGetValue(key, out var queue))
        {
            return new UserSendStats(tenantId, userId, 0, DateTimeOffset.UtcNow);
        }

        Trim(queue, DateTimeOffset.UtcNow);
        return new UserSendStats(tenantId, userId, queue.Count, DateTimeOffset.UtcNow);
    }

    private static void Trim(ConcurrentQueue<DateTimeOffset> queue, DateTimeOffset now)
    {
        var threshold = now.AddHours(-1);
        while (queue.TryPeek(out var ts) && ts < threshold)
        {
            queue.TryDequeue(out _);
        }
    }

    private static string BuildKey(string tenantId, string userId) => $"{tenantId}:{userId}";
}
