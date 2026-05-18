using System.Net;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Base class for providers that download a feed periodically and answer lookups
///     from an in-memory cache. Atomic cache swap on successful refresh; failed
///     refresh keeps the previous cache.
/// </summary>
internal abstract class ThreatIntelOfflineProviderBase : IThreatIntelProvider
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private volatile IpCidrCache? _cache;
    private DateTime _lastRefreshUtc;

    protected ThreatIntelOfflineProviderBase(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    public abstract string Name { get; }
    public ThreatIntelMode Mode => ThreatIntelMode.Offline;
    public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
    public abstract TimeSpan RefreshInterval { get; }

    /// <summary>Fully-qualified classification label for verdicts this provider emits.</summary>
    protected abstract string Classification { get; }

    /// <summary>Confidence emitted on a hit. Provider-specific (DROP = 0.95, EDROP = 0.9, etc.).</summary>
    protected virtual double HitConfidence => 0.9;

    /// <summary>Fetch + parse the upstream feed into a CIDR list. Each line is a CIDR string.</summary>
    protected abstract Task<IReadOnlyList<string>> FetchCidrsAsync(HttpClient http, CancellationToken ct);

    public ThreatIntelVerdict? TryLookup(ThreatSubject subject)
    {
        if (subject.Type != ThreatSubjectType.Ip) return null;
        var cache = _cache;
        if (cache is null) return null;
        if (!IPAddress.TryParse(subject.Value, out var ip)) return null;
        if (!cache.Contains(ip)) return null;

        return new ThreatIntelVerdict
        {
            Provider = Name,
            Classification = Classification,
            Confidence = HitConfidence,
            ObservedUtc = _lastRefreshUtc,
            // ExpiresUtc unset: offline feeds rely on RefreshAsync to invalidate the
            // whole cache on swap, not per-verdict TTLs.
            ExpiresUtc = default,
            Metadata = null
        };
    }

    public async Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
    {
        try
        {
            var cidrs = await FetchCidrsAsync(_http, cancellationToken);
            var next = new IpCidrCache(cidrs);
            _cache = next;
            _lastRefreshUtc = DateTime.UtcNow;
            _logger.LogInformation(
                "{Provider}: loaded {Count} CIDR entries", Name, next.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the previous cache. Coordinator surfaces age via the dashboard so
            // operators can spot stale intel.
            _logger.LogWarning(ex,
                "{Provider}: refresh failed; keeping previous cache ({Count} entries, last refreshed {LastRefresh:O})",
                Name, _cache?.Count ?? 0, _lastRefreshUtc);
        }
    }
}
