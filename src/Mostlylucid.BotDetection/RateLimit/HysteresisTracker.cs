using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     "True for N seconds" gate. Prevents tier transitions from flapping
///     when a degradation signal oscillates across a threshold.
/// </summary>
/// <remarks>
///     Adapted from PR #16. Used by <see cref="AdaptiveScalingTracker"/>
///     to gate "enter degraded tier" / "enter critical tier" transitions
///     on a configurable dwell time -- a one-request 5xx spike doesn't
///     halve the bot allowance.
/// </remarks>
public sealed class HysteresisTracker
{
    private readonly ConcurrentDictionary<string, DateTime> _firstTrueAt = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;

    public HysteresisTracker() : this(static () => DateTime.UtcNow) { }

    /// <summary>Test-only constructor: deterministic clock.</summary>
    public HysteresisTracker(Func<DateTime> clock)
    {
        _clock = clock;
    }

    /// <summary>
    ///     Returns <c>true</c> when <paramref name="conditionTrue"/> has been
    ///     continuously true for at least <paramref name="forSeconds"/>.
    ///     A single <c>false</c> resets the dwell timer to zero.
    /// </summary>
    public bool IsSatisfied(string ruleKey, bool conditionTrue, double forSeconds)
    {
        if (!conditionTrue)
        {
            _firstTrueAt.TryRemove(ruleKey, out _);
            return false;
        }

        var now = _clock();
        var firstTrue = _firstTrueAt.GetOrAdd(ruleKey, now);
        return (now - firstTrue).TotalSeconds >= forSeconds;
    }

    /// <summary>Reset every dwell timer (typically called on config reload).</summary>
    public void Reset() => _firstTrueAt.Clear();
}
