using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
/// Singleton service that passively records 4xx/5xx responses served to each signature.
/// <para>
/// ReactivePatternContributor reads from this tracker on subsequent requests to compute:
/// - Post-error gap compliance (did the client respect Retry-After?)
/// - Path persistence after 403 (kept probing the blocked path)
/// - Geometric retry patterns (exponential/linear backoff)
/// - Coordinated retry (multiple signatures hitting the same path simultaneously)
/// </para>
/// </summary>
public sealed class ReactiveSignalTracker
{
    private const int MaxEventsPerSignature = 40;
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    public readonly record struct ErrorEvent(
        int StatusCode,
        string Path,
        int? RetryAfterSeconds,
        DateTimeOffset ServedAt);

    private readonly ConcurrentDictionary<string, List<ErrorEvent>> _events = new();

    /// <summary>Record a 4xx/5xx response served to a signature.</summary>
    public void RecordErrorServed(string signature, int statusCode, string path, int? retryAfterSeconds)
    {
        if (string.IsNullOrEmpty(signature)) return;
        var ev = new ErrorEvent(statusCode, path, retryAfterSeconds, DateTimeOffset.UtcNow);
        _events.AddOrUpdate(signature,
            _ => [ev],
            (_, list) =>
            {
                lock (list)
                {
                    list.Add(ev);
                    if (list.Count > MaxEventsPerSignature)
                        list.RemoveAt(0);
                    return list;
                }
            });
    }

    /// <summary>Get the error history for a signature (oldest first, within MaxAge).</summary>
    public IReadOnlyList<ErrorEvent> GetHistory(string signature)
    {
        if (!_events.TryGetValue(signature, out var list)) return [];
        lock (list)
        {
            var cutoff = DateTimeOffset.UtcNow - MaxAge;
            return list.Where(e => e.ServedAt >= cutoff).ToList();
        }
    }

    /// <summary>
    /// Get signatures that received a 4xx on the given path at or after <paramref name="since"/>.
    /// Used for coordinated retry detection.
    /// </summary>
    public IReadOnlyList<string> GetCoRetriers(string path, DateTimeOffset since)
    {
        var result = new List<string>();
        foreach (var (sig, list) in _events)
        {
            lock (list)
            {
                if (list.Any(e => e.Path == path && e.ServedAt >= since))
                    result.Add(sig);
            }
        }
        return result;
    }

    /// <summary>Prune entries older than MaxAge (call periodically from a background service).</summary>
    public void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxAge;
        foreach (var (sig, list) in _events)
        {
            lock (list)
            {
                list.RemoveAll(e => e.ServedAt < cutoff);
            }
            if (list.Count == 0) _events.TryRemove(sig, out _);
        }
    }
}
