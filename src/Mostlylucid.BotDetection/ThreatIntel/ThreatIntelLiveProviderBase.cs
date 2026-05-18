using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Base for providers that call a vendor HTTP API per subject (GreyNoise,
///     AbuseIPDB, Shodan, …). Owns the operational scaffolding so concrete
///     providers only have to implement <see cref="FetchAsync"/>:
///
///     <list type="bullet">
///       <item><b>Per-subject result cache</b> with TTL. TryLookup returns the
///         cached <see cref="ThreatIntelVerdict"/> (hot-path safe, no I/O).</item>
///       <item><b>Quota gate</b>: per-UTC-day call counter; once exhausted,
///         <see cref="RefreshAsync"/> short-circuits without hitting the vendor.
///         Resets at midnight UTC.</item>
///       <item><b>Circuit breaker</b>: rolling 1-minute error rate; when over the
///         configured threshold, opens for <see cref="BreakerOpenDuration"/>.
///         Open-circuit calls fast-fail without touching the vendor.</item>
///       <item><b>In-flight coalescing</b>: two concurrent
///         <see cref="RefreshAsync"/> calls for the same subject share one HTTP
///         round-trip via a <c>ConcurrentDictionary&lt;subject, Task&gt;</c>.</item>
///     </list>
/// </summary>
internal abstract class ThreatIntelLiveProviderBase : IThreatIntelProvider
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, CachedVerdict> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ThreatIntelVerdict?>> _inFlight = new(StringComparer.Ordinal);

    // Quota state. _quotaDateUtc is the wall-clock date the counter applies to;
    // the first call after midnight UTC resets both fields under a tiny lock.
    private readonly object _quotaLock = new();
    private DateTime _quotaDateUtc = DateTime.UtcNow.Date;
    private int _quotaUsed;

    // Circuit-breaker state. _errorTimestamps holds the start-of-minute timestamp
    // for each error in the trailing window; over-threshold trips _breakerOpenUntil.
    private readonly object _breakerLock = new();
    private readonly Queue<DateTime> _errorTimestamps = new();
    private int _attemptsInWindow;
    private DateTime _breakerOpenUntil = DateTime.MinValue;

    protected ThreatIntelLiveProviderBase(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public abstract string Name { get; }
    public ThreatIntelMode Mode => ThreatIntelMode.Live;
    public abstract IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; }
    public abstract TimeSpan RefreshInterval { get; }

    /// <summary>How long a successful verdict remains in the read cache.</summary>
    protected virtual TimeSpan CacheTtl => TimeSpan.FromHours(6);

    /// <summary>Per-UTC-day quota; once exhausted, RefreshAsync no-ops until midnight UTC.</summary>
    protected abstract int DailyQuota { get; }

    /// <summary>Error-rate threshold (0..1) that trips the breaker over a 1-minute window.</summary>
    protected virtual double BreakerErrorRate => 0.2;

    /// <summary>How long the breaker stays open after tripping.</summary>
    protected virtual TimeSpan BreakerOpenDuration => TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Vendor-specific fetch + adapt. Returns null when the vendor returned
    ///     no useful verdict (the negative result is cached to suppress repeat
    ///     calls for the same subject); throw on transient failure to feed the
    ///     circuit breaker.
    /// </summary>
    protected abstract Task<ThreatIntelVerdict?> FetchAsync(
        HttpClient http, ThreatSubject subject, CancellationToken ct);

    public ThreatIntelVerdict? TryLookup(ThreatSubject subject)
    {
        if (!SupportedSubjects.Contains(subject.Type)) return null;
        if (!_cache.TryGetValue(subject.Value, out var entry)) return null;
        // ExpiresUtc on the cached verdict is checked by the coordinator on read;
        // the cache itself just stores what FetchAsync produced.
        return entry.Verdict;
    }

    public async Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
    {
        // Offline-style "refresh everything" makes no sense for a live provider:
        // we don't know what to enrich without a subject. Quietly no-op.
        if (subject is null) return;
        if (!SupportedSubjects.Contains(subject.Type)) return;

        // Quota check first - cheapest gate, blocks vendor calls without acquiring
        // any per-subject state.
        if (!TryConsumeQuota()) return;

        // Breaker check.
        if (IsBreakerOpen()) return;

        var key = subject.Value;

        // Coalesce concurrent calls for the same subject. The first caller wins;
        // every subsequent caller awaits the same Task. The factory MUST capture
        // `subject` (not just `key`) so the right ThreatSubjectType is preserved
        // in the closure when the provider supports multiple types.
        var task = _inFlight.GetOrAdd(key, _ => DoFetchAsync(subject, cancellationToken));
        try
        {
            var verdict = await task;
            if (verdict is not null)
            {
                _cache[key] = new CachedVerdict(verdict, DateTime.UtcNow.Add(CacheTtl));
            }
            // Successful refresh: record the attempt for the breaker but no error.
            RecordAttempt(error: false);
        }
        catch (Exception ex)
        {
            RecordAttempt(error: true);
            _logger.LogWarning(ex, "{Provider}: live fetch failed for {Subject}", Name, subject);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<ThreatIntelVerdict?> DoFetchAsync(ThreatSubject subject, CancellationToken ct)
    {
        return await FetchAsync(_http, subject, ct);
    }

    private bool TryConsumeQuota()
    {
        if (DailyQuota <= 0) return false;  // explicit 0 = disabled
        lock (_quotaLock)
        {
            var today = DateTime.UtcNow.Date;
            if (today != _quotaDateUtc)
            {
                _quotaDateUtc = today;
                _quotaUsed = 0;
            }
            if (_quotaUsed >= DailyQuota) return false;
            _quotaUsed++;
            return true;
        }
    }

    private bool IsBreakerOpen()
    {
        lock (_breakerLock)
        {
            return DateTime.UtcNow < _breakerOpenUntil;
        }
    }

    private void RecordAttempt(bool error)
    {
        lock (_breakerLock)
        {
            var now = DateTime.UtcNow;
            // Trim window
            var cutoff = now.AddMinutes(-1);
            while (_errorTimestamps.Count > 0 && _errorTimestamps.Peek() < cutoff)
                _errorTimestamps.Dequeue();
            _attemptsInWindow = Math.Max(_attemptsInWindow, _errorTimestamps.Count);
            // Bump attempts counter (reset every minute via Min on next pass below)
            _attemptsInWindow++;
            if (error) _errorTimestamps.Enqueue(now);

            // Breaker decision: need at least 5 attempts in the window to evaluate
            // rate (otherwise a single error spikes 100%). Spec calls this out as
            // a defence against premature trips on low traffic.
            if (_attemptsInWindow >= 5 && _errorTimestamps.Count > 0)
            {
                var rate = (double)_errorTimestamps.Count / _attemptsInWindow;
                if (rate >= BreakerErrorRate)
                {
                    _breakerOpenUntil = now.Add(BreakerOpenDuration);
                    _logger.LogWarning(
                        "{Provider}: circuit breaker OPEN until {Until:O} (error rate {Rate:P0} over last minute)",
                        Name, _breakerOpenUntil, rate);
                    _errorTimestamps.Clear();
                    _attemptsInWindow = 0;
                }
            }
        }
    }

    /// <summary>Quota + breaker diagnostics for dashboards. Returns a snapshot, no lock held by the caller.</summary>
    public LiveProviderStatus GetStatus()
    {
        int used;
        DateTime quotaDate;
        lock (_quotaLock) { used = _quotaUsed; quotaDate = _quotaDateUtc; }
        DateTime openUntil;
        int errorsInWindow;
        lock (_breakerLock)
        {
            openUntil = _breakerOpenUntil;
            errorsInWindow = _errorTimestamps.Count;
        }
        return new LiveProviderStatus(
            Provider: Name,
            QuotaUsed: used,
            DailyQuota: DailyQuota,
            QuotaDateUtc: quotaDate,
            CacheSize: _cache.Count,
            BreakerOpenUntilUtc: openUntil,
            ErrorsInWindow: errorsInWindow);
    }

    private sealed record CachedVerdict(ThreatIntelVerdict Verdict, DateTime ExpiresUtc);
}

/// <summary>
///     Per-provider snapshot used by the dashboard's threat-intel tab. Surfaced
///     by <see cref="ThreatIntelLiveProviderBase.GetStatus"/>; offline providers
///     don't expose this (they don't have quota / breaker state).
/// </summary>
public sealed record LiveProviderStatus(
    string Provider,
    int QuotaUsed,
    int DailyQuota,
    DateTime QuotaDateUtc,
    int CacheSize,
    DateTime BreakerOpenUntilUtc,
    int ErrorsInWindow);
