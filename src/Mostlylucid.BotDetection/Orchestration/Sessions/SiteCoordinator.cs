using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.SlidingCache;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Tunables for the signals-native session layer (the <see cref="SiteCoordinator"/>
///     rearchitecture that replaces the partitioned <c>SessionStore</c> dict).
///     All knobs are operator-configurable per <c>feedback_all_settings_configurable</c>.
/// </summary>
public sealed class SessionCoordinatorOptions
{
    /// <summary>
    ///     Hard cap on concurrently-tracked sessions per site. This is THE memory
    ///     bound: working set plateaus at roughly <c>MaxSessions × per-session
    ///     footprint</c> regardless of arrival cardinality. Enforced by the
    ///     underlying <see cref="SlidingCacheAtom{TKey,TResult}"/> (eviction of the
    ///     lowest-priority session on overflow), not a periodic sweep.
    /// </summary>
    public int MaxSessions { get; set; } = 5_000;

    /// <summary>
    ///     A session's sliding-expiration window: reset on every request, so an
    ///     active session stays live and writeable; once a session sees no activity
    ///     for this long it retires (and summarizes to durable in a later slice).
    /// </summary>
    public TimeSpan SessionInactivityWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Absolute max lifetime for a session regardless of activity.</summary>
    public TimeSpan SessionMaxLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    ///     Cap on distinct sites (domains) tracked concurrently. The site key is the
    ///     Host header, which is attacker-controllable, so the registry is bounded
    ///     too: once this many sites are live, contributions for a new, unseen site
    ///     are dropped rather than minting an unbounded number of coordinators.
    /// </summary>
    public int MaxSites { get; set; } = 256;
}

/// <summary>
///     One useful contribution escalated from a completed request into its session:
///     the filtered, decision-relevant signal payload of that request. Slice 1
///     carries it raw; projection molecules compress it on read/persist in later
///     slices (per "signals + compression, all the way up"). No pre-aggregation.
/// </summary>
/// <param name="RequestId">Opaque per-request id (for internal bounding + dedup later).</param>
/// <param name="At">When the request completed.</param>
/// <param name="Signals">The useful signal strings this request contributed.</param>
public sealed record SessionContribution(
    string RequestId,
    DateTimeOffset At,
    IReadOnlyList<string> Signals);

/// <summary>
///     An ephemeral session instance — the value held in a site's session sliding
///     cache. A session <em>is</em> its accumulated request contributions; readers
///     project a view on demand (later slices) rather than reading a stored
///     aggregate. Lifetime is owned by the <see cref="SlidingCacheAtom{TKey,TResult}"/>
///     (sliding-expiration on inactivity + hard cap on count), so when a session
///     retires its state goes with it — no separate record to evict, no leak.
/// </summary>
public sealed class Session
{
    private readonly object _gate = new();
    private readonly List<SessionContribution> _contributions = new();

    public Session(string sessionKey)
    {
        SessionKey = sessionKey;
        Created = DateTimeOffset.UtcNow;
        LastActivityAt = Created;
    }

    /// <summary>The session key (siteId is the coordinator; this is the fingerprint).</summary>
    public string SessionKey { get; }

    public DateTimeOffset Created { get; }

    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Files a request's useful contribution into this session.</summary>
    public void Contribute(SessionContribution contribution)
    {
        lock (_gate)
        {
            _contributions.Add(contribution);
            LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Point-in-time snapshot of the contributing requests (for projection).</summary>
    public IReadOnlyList<SessionContribution> SnapshotContributions()
    {
        lock (_gate)
        {
            return _contributions.ToArray();
        }
    }

    public int ContributionCount
    {
        get
        {
            lock (_gate)
            {
                return _contributions.Count;
            }
        }
    }
}

/// <summary>
///     One per site (domain). Holds the shared site <see cref="SignalSink"/> and
///     the per-fingerprint session sliding cache. Bounded + self-draining by
///     construction: <see cref="SessionCoordinatorOptions.MaxSessions"/> caps the
///     concurrent session count and the sliding cache retires inactive sessions —
///     no partitioned dict-of-dicts, no 30-second cleanup sweep, no bytes-estimate.
/// </summary>
/// <remarks>
///     Slice 1 of the session rearchitecture: this stands ALONGSIDE the existing
///     <c>SessionStore</c>. Readers do not migrate yet; the bridge dual-writes
///     contributions here so the ephemeral path accumulates in parallel. The
///     importance-based retention scorer and the summarize→escalator→durable path
///     land in later slices; Slice 1 uses a flat retention stub.
/// </remarks>
public sealed class SiteCoordinator : IAsyncDisposable
{
    private readonly SlidingCacheAtom<string, Session> _sessions;

    public SiteCoordinator(string siteId, SessionCoordinatorOptions options, SignalSink? siteSink = null)
    {
        SiteId = siteId;
        SiteSink = siteSink ?? new SignalSink(Math.Max(2, options.MaxSessions * 2), options.SessionInactivityWindow);

        _sessions = new SlidingCacheAtom<string, Session>(
            factory: static (key, _) => Task.FromResult(new Session(key)),
            slidingExpiration: options.SessionInactivityWindow,
            absoluteExpiration: options.SessionMaxLifetime,
            maxSize: options.MaxSessions,
            signals: SiteSink,
            // Slice 1 stub: flat priority. Importance scoring (bot-suspect / novelty /
            // uncertainty ranks HIGH; confident-human ranks LOW) lands in a later slice.
            retentionScorer: static (_, _) => 0.5);
    }

    /// <summary>The site (domain) this coordinator owns.</summary>
    public string SiteId { get; }

    /// <summary>The shared site sink — the reference bag for cross-session queries.</summary>
    public SignalSink SiteSink { get; }

    /// <summary>Live count of tracked (non-expired) sessions (bounded by <c>MaxSessions</c>).</summary>
    public int SessionCount => _sessions.GetStats().ValidEntries;

    /// <summary>
    ///     Escalates a completed request's useful signals into its session, creating
    ///     the session on first contact. Resets the session's sliding expiration.
    /// </summary>
    public async Task ContributeAsync(string fingerprintId, SessionContribution contribution, CancellationToken ct = default)
    {
        var session = await _sessions.GetOrComputeAsync(fingerprintId, ct).ConfigureAwait(false);
        session.Contribute(contribution);
    }

    /// <summary>Reads the current session for a fingerprint without creating one.</summary>
    public bool TryGetSession(string fingerprintId, out Session? session)
        => _sessions.TryGet(fingerprintId, out session);

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();
}
