using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

public sealed class HysteresisTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _firstTrueAt = new(StringComparer.Ordinal);

    public bool IsSatisfied(string ruleKey, bool conditionTrue, double forSeconds)
    {
        if (!conditionTrue)
        {
            _firstTrueAt.TryRemove(ruleKey, out _);
            return false;
        }

        var now = DateTime.UtcNow;
        var firstTrue = _firstTrueAt.GetOrAdd(ruleKey, now);
        return (now - firstTrue).TotalSeconds >= forSeconds;
    }

    public void Reset() => _firstTrueAt.Clear();

    internal void ForceFirstTrue(string ruleKey, DateTime firstTrueAt)
    {
        _firstTrueAt[ruleKey] = firstTrueAt;
    }
}
