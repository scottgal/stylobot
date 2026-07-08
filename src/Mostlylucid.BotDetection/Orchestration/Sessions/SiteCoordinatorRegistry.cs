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
    private readonly ConcurrentDictionary<string, SiteCoordinator> _coordinators = new(StringComparer.Ordinal);
    private readonly SessionCoordinatorOptions _options;
    private readonly ILogger<SiteCoordinatorRegistry> _logger;

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
        if (_coordinators.TryGetValue(siteId, out var existing))
            return existing;

        if (_coordinators.Count >= _options.MaxSites)
            return null; // site cap reached; drop rather than grow unbounded

        return _coordinators.GetOrAdd(siteId, id => new SiteCoordinator(id, _options));
    }

    /// <summary>Reads a site's coordinator without creating one.</summary>
    public bool TryGet(string siteId, out SiteCoordinator? coordinator)
        => _coordinators.TryGetValue(siteId, out coordinator);

    /// <summary>
    ///     Fire-and-forget write of a request contribution into the site's session.
    ///     Non-blocking on the caller (the hot request path); bounded by the
    ///     coordinator's session cache; exceptions are swallowed. Used for
    ///     out-of-band metadata writes (e.g. the Web Bot Auth verdict) that must
    ///     not raise on the aggregate change stream.
    /// </summary>
    public void Contribute(string siteId, string fingerprintId, SessionContribution contribution)
    {
        var coordinator = GetOrCreate(siteId);
        if (coordinator is null) return;

        _ = ContributeSafeAsync(coordinator, fingerprintId, contribution);
    }

    private async Task ContributeSafeAsync(SiteCoordinator coordinator, string fingerprintId, SessionContribution contribution)
    {
        try
        {
            await coordinator.ContributeAsync(fingerprintId, contribution).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SiteCoordinator dual-write failed for site={Site} fp={Fp} (additive path; ignored)",
                coordinator.SiteId, fingerprintId);
        }
    }

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
