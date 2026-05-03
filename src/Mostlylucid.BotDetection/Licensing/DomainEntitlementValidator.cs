using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Matches an incoming request's host against the set of domains licensed for this
///     StyloBot install. Implements warn-never-lock enforcement: mismatches bump a counter
///     and surface in the dashboard, but the request itself is never affected and core
///     detection always runs.
///
///     Matching rules (see <c>stylobot-commercial/docs/licensing-simplified.md</c>):
///     - eTLD+1 match: a license covering <c>acme.com</c> grants any <c>*.acme.com</c>.
///       For now we do a naive suffix match; swap for a real PSL library if customer
///       feedback surfaces edge cases (e.g., <c>acme.co.uk</c>).
///     - Localhost + loopback + *.test / *.local / *.localdomain always allowed.
///     - Explicit host-only entries (prefix with <c>=</c> in the license, e.g., <c>=admin.acme.com</c>)
///       only match exactly that host, no subdomain wildcard.
///     - No allowed domains configured → no enforcement, no warnings. OSS tier default.
///
///     The validator's <see cref="GetStatistics"/> method feeds the dashboard license card;
///     counters are cheap (Interlocked increments on small integers).
/// </summary>
public sealed class DomainEntitlementValidator
{
    private readonly string[] _allowedWildcardDomains;
    private readonly string[] _allowedExactDomains;
    private long _requestsLicensed;
    private long _requestsMismatched;
    private long _requestsCloudPool;
    private readonly ConcurrentDictionary<string, long> _mismatchedHosts = new();

    public DomainEntitlementValidator(IEnumerable<string>? licensedDomains)
    {
        var wild = new List<string>();
        var exact = new List<string>();

        foreach (var d in licensedDomains ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            var trimmed = d.Trim();
            if (trimmed.StartsWith('='))
                exact.Add(CloudPoolHosts.NormalizeHost(trimmed[1..]));
            else
                wild.Add(CloudPoolHosts.NormalizeHost(trimmed));
        }

        _allowedWildcardDomains = wild.ToArray();
        _allowedExactDomains = exact.ToArray();
    }

    /// <summary>
    ///     True if the license is configured with any domains - when false, the validator
    ///     runs in pass-through mode (no counting, no warnings). OSS / unconfigured state.
    /// </summary>
    public bool IsEnforcing => _allowedWildcardDomains.Length > 0 || _allowedExactDomains.Length > 0;

    /// <summary>
    ///     Classify a single host. Increments internal counters. Never throws, never affects
    ///     the request flow. Return value is advisory only.
    /// </summary>
    public DomainEntitlementResult Check(string? host)
    {
        if (!IsEnforcing) return DomainEntitlementResult.NotEnforced;

        var normalized = CloudPoolHosts.NormalizeHost(host ?? string.Empty);
        if (string.IsNullOrEmpty(normalized)) return DomainEntitlementResult.NoHost;

        // Always-allowed: dev hosts.
        if (IsAlwaysAllowed(normalized))
        {
            Interlocked.Increment(ref _requestsLicensed);
            return DomainEntitlementResult.Licensed;
        }

        // Exact host match.
        foreach (var e in _allowedExactDomains)
        {
            if (string.Equals(normalized, e, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _requestsLicensed);
                return DomainEntitlementResult.Licensed;
            }
        }

        // Wildcard / eTLD+1 match.
        foreach (var w in _allowedWildcardDomains)
        {
            if (string.Equals(normalized, w, StringComparison.Ordinal) ||
                normalized.EndsWith("." + w, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _requestsLicensed);
                return DomainEntitlementResult.Licensed;
            }
        }

        // Mismatched - classify further for dashboard granularity.
        Interlocked.Increment(ref _requestsMismatched);
        _mismatchedHosts.AddOrUpdate(normalized, 1, (_, v) => v + 1);

        if (CloudPoolHosts.IsCloudPoolHost(normalized))
        {
            Interlocked.Increment(ref _requestsCloudPool);
            return DomainEntitlementResult.MismatchCloudPool;
        }

        return DomainEntitlementResult.Mismatch;
    }

    /// <summary>
    ///     Hosts always allowed regardless of license - dev environments, loopback.
    ///     Matches cover both literal and subdomain use.
    /// </summary>
    private static bool IsAlwaysAllowed(string host)
    {
        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0") return true;
        if (host.EndsWith(".localhost", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".test", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".local", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".localdomain", StringComparison.Ordinal)) return true;
        if (host.EndsWith(".internal", StringComparison.Ordinal)) return true;
        if (host.StartsWith("127.", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Snapshot the counters for the admin dashboard license card.</summary>
    public DomainEntitlementStats GetStatistics()
    {
        var licensed = Interlocked.Read(ref _requestsLicensed);
        var mismatched = Interlocked.Read(ref _requestsMismatched);
        var pool = Interlocked.Read(ref _requestsCloudPool);
        var total = licensed + mismatched;
        var mismatchRatio = total == 0 ? 0.0 : (double)mismatched / total;

        // Top 5 unfamiliar hosts by volume, trimmed to something dashboard-friendly.
        var topUnfamiliar = _mismatchedHosts
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => new HostObservation(kv.Key, kv.Value))
            .ToList();

        return new DomainEntitlementStats(
            IsEnforcing: IsEnforcing,
            AllowedDomains: _allowedWildcardDomains.Concat(_allowedExactDomains.Select(e => "=" + e)).ToArray(),
            RequestsLicensed: licensed,
            RequestsMismatched: mismatched,
            RequestsCloudPool: pool,
            MismatchRatio: mismatchRatio,
            TopUnfamiliarHosts: topUnfamiliar);
    }
}

/// <summary>One-word advice on what to show in the dashboard for this host.</summary>
public enum DomainEntitlementResult
{
    /// <summary>No domains configured on this install - validator is pass-through.</summary>
    NotEnforced,
    /// <summary>Host matched a licensed domain (or the always-allow list).</summary>
    Licensed,
    /// <summary>Host didn't match; operator likely needs to add it to the license.</summary>
    Mismatch,
    /// <summary>Host is a shared cloud-pool platform hostname - warn but don't escalate.</summary>
    MismatchCloudPool,
    /// <summary>Host header was empty / malformed - should be rare behind a real reverse proxy.</summary>
    NoHost
}

public sealed record DomainEntitlementStats(
    bool IsEnforcing,
    IReadOnlyList<string> AllowedDomains,
    long RequestsLicensed,
    long RequestsMismatched,
    long RequestsCloudPool,
    double MismatchRatio,
    IReadOnlyList<HostObservation> TopUnfamiliarHosts);

public sealed record HostObservation(string Host, long Hits);