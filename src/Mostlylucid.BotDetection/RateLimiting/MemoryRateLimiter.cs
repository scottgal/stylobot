namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     <see cref="IRateLimiter"/> implementation that keys buckets by the
///     joined subject set and delegates the bucket math to an injected
///     <see cref="IRateLimitStateStore"/>. The store boundary is what lets
///     commercial swap in Redis without this class changing.
/// </summary>
public sealed class MemoryRateLimiter : IRateLimiter
{
    private readonly IRateLimitStateStore _store;

    public MemoryRateLimiter(IRateLimitStateStore store)
    {
        _store = store;
    }

    public bool TryAcquire(IReadOnlyList<RateLimitSubject> subjects, RateLimitConfig config, int tokens = 1)
    {
        if (subjects is null || subjects.Count == 0) return true;
        var key = BuildKey(subjects);
        return _store.TryAcquire(key, config.BurstSize, config.TokensPerSecond, tokens);
    }

    public RateLimitState GetState(IReadOnlyList<RateLimitSubject> subjects, RateLimitConfig config)
    {
        var key = BuildKey(subjects);
        return _store.Peek(key) ?? new RateLimitState(
            config.BurstSize, config.BurstSize, config.TokensPerSecond, DateTime.UtcNow);
    }

    /// <summary>
    ///     Compose the bucket key from the subject set. Order-independent --
    ///     a rule with [BotType=Scraper, Country=US] and a rule with
    ///     [Country=US, BotType=Scraper] target the same composite bucket.
    /// </summary>
    private static string BuildKey(IReadOnlyList<RateLimitSubject> subjects)
    {
        if (subjects.Count == 1) return subjects[0].ToString();
        var sorted = subjects.OrderBy(s => s.Kind).ThenBy(s => s.Value).Select(s => s.ToString());
        return string.Join("|", sorted);
    }
}
