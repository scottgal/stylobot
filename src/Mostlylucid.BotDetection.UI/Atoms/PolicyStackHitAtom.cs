using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Options;

namespace Mostlylucid.BotDetection.UI.Atoms;

public sealed class PolicyStackHitAtom
{
    private readonly TimeProvider _clock;
    private readonly PolicyStackHitAtomOptions _options;
    private readonly ConcurrentDictionary<string, ScopeBucket> _scopes = new();

    public PolicyStackHitAtom(TimeProvider clock, IOptions<PolicyStackHitAtomOptions> options)
    {
        _clock = clock;
        _options = options.Value;
    }

    public int ScopeCount => _scopes.Count;

    public void Record(string scope, PolicyIntentKind intent)
    {
        var now = _clock.GetUtcNow();

        if (_scopes.Count >= _options.MaxScopes && !_scopes.ContainsKey(scope))
        {
            var oldestKey = default(string);
            var oldestAt = DateTimeOffset.MaxValue;
            foreach (var kv in _scopes)
            {
                var touchedAt = kv.Value.LastTouchedAt;
                if (touchedAt < oldestAt)
                {
                    oldestAt = touchedAt;
                    oldestKey = kv.Key;
                }
            }
            if (oldestKey is not null) _scopes.TryRemove(oldestKey, out _);
        }

        var bucket = _scopes.GetOrAdd(scope, _ => new ScopeBucket());
        bucket.Add(intent, now);
    }

    public PolicyStackHitSnapshot Snapshot(string scope, TimeSpan window)
    {
        if (!_scopes.TryGetValue(scope, out var bucket))
        {
            return new PolicyStackHitSnapshot(new Dictionary<PolicyIntentKind, int>());
        }
        return bucket.Snapshot(_clock.GetUtcNow() - window);
    }

    public void AgeOut()
    {
        var cutoff = _clock.GetUtcNow() - _options.RetentionWindow;
        foreach (var (_, bucket) in _scopes) bucket.AgeOut(cutoff);
    }

    private sealed class ScopeBucket
    {
        // Lock the hit list because Record / Snapshot / AgeOut can fire concurrently
        // from the request hot path, dashboard reads, and the tick.1m subscription.
        private readonly List<(DateTimeOffset At, PolicyIntentKind Intent)> _hits = new();
        private long _lastTouchedAtTicks;

        public DateTimeOffset LastTouchedAt => new(Volatile.Read(ref _lastTouchedAtTicks), TimeSpan.Zero);

        public void Add(PolicyIntentKind intent, DateTimeOffset at)
        {
            lock (_hits)
            {
                _hits.Add((at, intent));
                Volatile.Write(ref _lastTouchedAtTicks, at.UtcTicks);
            }
        }

        public PolicyStackHitSnapshot Snapshot(DateTimeOffset since)
        {
            var counts = new Dictionary<PolicyIntentKind, int>();
            lock (_hits)
            {
                foreach (var hit in _hits)
                {
                    if (hit.At >= since)
                        counts[hit.Intent] = counts.GetValueOrDefault(hit.Intent) + 1;
                }
            }
            return new PolicyStackHitSnapshot(counts);
        }

        public void AgeOut(DateTimeOffset cutoff)
        {
            lock (_hits) _hits.RemoveAll(h => h.At < cutoff);
        }
    }
}

public sealed record PolicyStackHitSnapshot(IReadOnlyDictionary<PolicyIntentKind, int> Counts);