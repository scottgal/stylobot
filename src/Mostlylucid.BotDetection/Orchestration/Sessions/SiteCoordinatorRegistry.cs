using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Holds one <see cref="SiteCoordinator"/> per site (domain), created lazily.
///     Singleton. This is the bounded backing store for the signals-native session
///     layer: <see cref="SessionStore"/> routes every upsert and read through here,
///     so the aggregate is reconstructed from a bounded session rather than held in
///     an unbounded per-site dictionary.
/// </summary>
/// <remarks>
///     Bounded on BOTH axes: the number of sites is capped by
///     <see cref="SessionCoordinatorOptions.MaxSites"/> (the site key is the
///     attacker-controllable Host header), and each coordinator's session set is
///     capped by <see cref="SessionCoordinatorOptions.MaxSessions"/>. So a
///     high-cardinality flood on either fingerprint OR host stays bounded.
/// </remarks>
public sealed class SiteCoordinatorRegistry : IAsyncDisposable
{
    // Host keys are case-insensitive (RFC 3986 §3.2.2); Example.com and example.com
    // are the same site, so case-fold the key or a case-flipping attacker multiplies
    // cardinality and splits one site's sessions across coordinators.
    private readonly ConcurrentDictionary<string, SiteCoordinator> _coordinators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _createLock = new();
    private readonly SessionCoordinatorOptions _options;
    private readonly ILogger<SiteCoordinatorRegistry> _logger;

    /// <summary>
    ///     Set by the canonical <see cref="SessionStore"/> before request traffic
    ///     begins. New site coordinators pass this loss-free cache-death handoff to
    ///     their bounded session atom.
    /// </summary>
    internal Func<string, string, Session, CancellationToken, ValueTask>? OnSessionEvicted { get; set; }

    public SiteCoordinatorRegistry(
        IOptions<SessionCoordinatorOptions> options,
        ILogger<SiteCoordinatorRegistry> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Number of sites currently tracked (bounded by <c>MaxSites</c>).</summary>
    public int SiteCount => _coordinators.Count;

    /// <summary>
    ///     Returns the coordinator for a site, creating it on first contact —
    ///     unless the site cap is reached and the site is new, in which case null
    ///     is returned (the contribution is dropped, keeping the registry bounded).
    /// </summary>
    public SiteCoordinator? GetOrCreate(string siteId)
    {
        // Hot path: an existing site resolves lock-free.
        if (_coordinators.TryGetValue(siteId, out var existing))
            return existing;

        // Cold path: a genuinely new site. Serialize creation so MaxSites is a HARD
        // bound - a non-atomic Count-then-Add lets a host-flood race N threads past
        // the check and add beyond the cap. The lock is taken only when a new site
        // first appears, never on the hot existing-site path above.
        lock (_createLock)
        {
            if (_coordinators.TryGetValue(siteId, out existing))
                return existing;

            if (_coordinators.Count >= _options.MaxSites)
            {
                // Site cap reached; drop rather than grow unbounded. The site key is the
                // attacker-controllable Host header, so hitting this is a host-flood signal.
                _logger.LogDebug(
                    "SiteCoordinatorRegistry at MaxSites={MaxSites}; dropping new site {SiteId}",
                    _options.MaxSites, siteId);
                return null;
            }

            var created = new SiteCoordinator(
                siteId,
                _options,
                onSessionEvicted: (fingerprintId, session, ct) =>
                    OnSessionEvicted?.Invoke(siteId, fingerprintId, session, ct) ?? ValueTask.CompletedTask);
            _coordinators[siteId] = created;
            return created;
        }
    }

    /// <summary>Reads a site's coordinator without creating one.</summary>
    public bool TryGet(string siteId, out SiteCoordinator? coordinator)
        => _coordinators.TryGetValue(siteId, out coordinator);

    public async ValueTask DisposeAsync()
    {
        foreach (var coordinator in _coordinators.Values)
        {
            try { await coordinator.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort dispose */ }
        }

        _coordinators.Clear();
    }
}
