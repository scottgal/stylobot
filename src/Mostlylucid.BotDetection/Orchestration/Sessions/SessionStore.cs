using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Auth;
using Mostlylucid.BotDetection.Orchestration.Sessions.Molecules;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Shared per-domain session store, signals-native + bounded. The
///     per-fingerprint aggregate is NOT a stored object: it is reconstructed on
///     demand from the bounded <see cref="SiteCoordinator"/> session's latest
///     snapshot signal (see <see cref="SessionAggregateMolecule"/>). Memory is
///     bounded by construction — the underlying <c>SlidingCacheAtom</c> evicts
///     at-insert — so the store no longer holds an unbounded per-site aggregate
///     dictionary and there is no janitor sweep.
/// </summary>
/// <remarks>
///     <para>
///         <b>Change notifications</b> — every upsert reconstructs + merges the
///         aggregate and raises it on <see cref="Changes"/>, so the on-change
///         persistence chain (<see cref="SessionAtom"/> reactor) is unchanged.
///     </para>
///     <para>
///         <b>Lifecycle / Finalizations</b> are retained for subscriber
///         compatibility but dormant: the on-eviction finalize/echo path is
///         deferred to the escalator wiring (which needs a
///         <c>SlidingCacheAtom.onEvict</c> callback). On-change persistence via
///         <see cref="Changes"/> is unaffected.
///     </para>
/// </remarks>
public sealed class SessionStore : IDisposable
{
    private readonly ILogger<SessionStore> _logger;
    private readonly SiteCoordinatorRegistry _sessions; // the bounded store; never null
    private readonly SessionStoreOptions _options;
    private readonly bool _ownsRegistry;
    private int _disposed;

    /// <summary>
    ///     Per-(site, fingerprint) last-contribution timestamps — the store's shadow
    ///     index for the finalize set (max-lifetime chunk + idle sweep, 2026-08-15).
    ///     The bounded cache owns the Session values but exposes no enumeration, so
    ///     this index is what the sweep scans; entries are removed on the cache's
    ///     onEvict finalize (the single finalize owner). Bounded by the cache cap.
    /// </summary>
    private readonly ConcurrentDictionary<(string SiteId, string FingerprintId), DateTimeOffset> _lastActivity
        = new();

    /// <summary>
    ///     Change-stream sink. Every upsert raises the freshly-merged aggregate.
    ///     The session atom hooks <c>TypedSignalRaised</c> to react to mutations.
    /// </summary>
    public TypedSignalSink<SessionAggregate> Changes { get; }

    /// <summary>Post-fold observations. Remote applies are intentionally silent.</summary>
    public TypedSignalSink<SessionFoldObservation> Observations { get; }

    /// <summary>Retained for subscriber compatibility; dormant (see remarks).</summary>
    public TypedSignalSink<SessionFinalizingSignal> Lifecycle { get; }

    /// <summary>Retained for subscriber compatibility; dormant (see remarks).</summary>
    public TypedSignalSink<SessionFinalizedAckSignal> Finalizations { get; }

    public SessionStore(
        IOptions<SessionStoreOptions> options,
        ILogger<SessionStore> logger,
        StyloFlow.Orchestration.IInitSignalBus? initSignalBus = null,
        SiteCoordinatorRegistry? siteCoordinators = null)
    {
        _logger = logger;
        _options = options.Value;

        if (siteCoordinators is not null)
        {
            _sessions = siteCoordinators;
            _ownsRegistry = false;
        }
        else
        {
            // Direct construction (tests / minimal hosts): own a default bounded registry.
            _sessions = new SiteCoordinatorRegistry(
                Options.Create(new SessionCoordinatorOptions()),
                NullLogger<SiteCoordinatorRegistry>.Instance);
            _ownsRegistry = true;
        }

        // The registry creates coordinators lazily, so wiring this before the first
        // upsert makes the bounded SlidingCacheAtom's onEvict hook the single
        // lifecycle owner for every session death (TTL, pressure, shutdown).
        _sessions.OnSessionEvicted = OnSessionEvictedAsync;

        const int sinkCap = 4096;
        Changes = new TypedSignalSink<SessionAggregate>(
            new SignalSink(maxCapacity: sinkCap, maxAge: _options.Ttl),
            maxCapacity: sinkCap, maxAge: _options.Ttl);
        Observations = new TypedSignalSink<SessionFoldObservation>(
            new SignalSink(maxCapacity: sinkCap, maxAge: _options.Ttl),
            maxCapacity: sinkCap, maxAge: _options.Ttl);
        Lifecycle = new TypedSignalSink<SessionFinalizingSignal>(
            new SignalSink(maxCapacity: sinkCap, maxAge: TimeSpan.FromMinutes(5)),
            maxCapacity: sinkCap, maxAge: TimeSpan.FromMinutes(5));
        Finalizations = new TypedSignalSink<SessionFinalizedAckSignal>(
            new SignalSink(maxCapacity: sinkCap, maxAge: TimeSpan.FromMinutes(5)),
            maxCapacity: sinkCap, maxAge: TimeSpan.FromMinutes(5));

        if (initSignalBus is not null)
        {
            var initFired = 0;
            Changes.TypedSignalRaised += _ =>
            {
                if (Interlocked.Exchange(ref initFired, 1) == 0)
                    initSignalBus.Raise(SessionStoreOptions.InitSignal);
            };
        }

        _logger.LogInformation(
            "SessionStore initialised (signals-native, bounded via SiteCoordinator): TTL {Ttl}",
            _options.Ttl);
    }

    /// <summary>
    ///     Upsert a <see cref="SessionSample"/> into the session for its
    ///     fingerprint on its site. Reconstructs the previous aggregate from the
    ///     bounded session, merges, writes the new snapshot back, raises it on
    ///     <see cref="Changes"/>, and returns it.
    /// </summary>
    public async Task<SessionAggregate> UpsertAsync(SessionSample sample, CancellationToken ct = default)
    {
        var coordinator = _sessions.GetOrCreate(sample.SiteId);
        if (coordinator is null)
        {
            // Site cap reached — return a transient aggregate (not stored). Keeps the
            // registry bounded on the Host axis; still notifies Changes.
            var transient = SessionAggregateMerge.FromFirstSample(sample);
            Changes.Raise(SessionSignalKeys.AggregateUpdated.Name, transient, sample.FingerprintId);
            Observations.Raise(SessionSignalKeys.Observation.Name,
                new SessionFoldObservation(transient, SessionFoldOrigin.LocalFragment), sample.FingerprintId);
            return transient;
        }

        var session = await coordinator.GetOrCreateSessionAsync(sample.FingerprintId, ct).ConfigureAwait(false);

        // The max-lifetime CHUNK (operator 2026-08-15: "even for continuous we can
        // chunk them"): the never-idle class (5-min pings, always-on clients, the rig's
        // continuous driver) would otherwise accumulate forever with no boundary and
        // never leave a trace — neither the gap nor the sliding TTL ever fires. A
        // request whose session's age from creation exceeds MaxLifetime finalizes the
        // epoch (the Lifecycle finalize chain — raised here; the cache's Invalidate is
        // SILENT, so there is no second finalize) and starts fresh; THIS sample
        // continues in the new epoch. Retrogressive, request-driven — same shape as
        // the legacy chunk. Rig-visible Information discriminator.
        var age = sample.Timestamp - session.Created;
        if (age >= _options.MaxLifetime)
        {
            _logger.LogInformation(
                "Session max-lifetime chunk for {FingerprintId} on {SiteId}: age {Age:hh\\:mm\\:ss} >= {MaxLifetime:hh\\:mm\\:ss} — finalizing epoch, starting fresh",
                sample.FingerprintId, sample.SiteId, age, _options.MaxLifetime);
            _lastActivity.TryRemove((sample.SiteId, sample.FingerprintId), out _);
            RaiseFinalize(sample.SiteId, sample.FingerprintId, session, SessionFinalizeReason.MaxLifetime);
            coordinator.InvalidateSession(sample.FingerprintId);
            session = await coordinator.GetOrCreateSessionAsync(sample.FingerprintId, ct).ConfigureAwait(false);
        }

        var previous = session.LocalAggregate
            ?? SessionAggregateMolecule.FromSession(session, sample.FingerprintId, sample.SiteId);
        var merged = previous is null
            ? SessionAggregateMerge.FromFirstSample(sample)
            : SessionAggregateMerge.Merge(previous, sample);

        session.SetLocalAggregate(merged);
        var folded = Fold(session, merged);
        session.Contribute(new SessionContribution(
            RequestId: sample.RequestId ?? $"{sample.FingerprintId}:{merged.SampleCount}",
            At: sample.Timestamp,
            Signals: new[] { SessionAggregateMolecule.ToSignal(folded) }));

        _lastActivity[(sample.SiteId, sample.FingerprintId)] = sample.Timestamp;

        Changes.Raise(SessionSignalKeys.AggregateUpdated.Name, folded, sample.FingerprintId);
        Observations.Raise(SessionSignalKeys.Observation.Name,
            new SessionFoldObservation(folded, SessionFoldOrigin.LocalFragment), sample.FingerprintId);
        return folded;
    }

    /// <summary>
    /// Applies an already-folded cumulative contribution silently. The cursor is
    /// retained by the bounded Session, so duplicate, reordered and stale input is
    /// a no-op and a rejected apply never consumes the incoming cursor. Remote
    /// observations are silent on the legacy <see cref="Changes"/> stream.
    /// </summary>
    public async Task<SessionAggregate?> ApplyRemoteFoldAsync(
        SessionAggregate aggregate,
        SessionFoldCursor cursor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor.EpochId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cursor.FencingGeneration, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cursor.Sequence, 0);

        var coordinator = _sessions.GetOrCreate(aggregate.SiteId);
        if (coordinator is null) return null;
        var session = await coordinator.GetOrCreateSessionAsync(aggregate.FingerprintId, ct).ConfigureAwait(false);
        var result = session.ApplyRemoteAtomically(cursor, aggregate,
            aggregates => SessionAggregateMerge.Fold(aggregates));
        if (!result.Applied) return result.Aggregate;
        Observations.Raise(SessionSignalKeys.Observation.Name,
            new SessionFoldObservation(result.Aggregate, SessionFoldOrigin.RemoteCanonical, cursor), aggregate.FingerprintId);
        return result.Aggregate;
    }

    private static SessionAggregate Fold(Session session, SessionAggregate fallback)
    {
        var local = session.LocalAggregate;
        var remote = session.RemoteAggregate;
        return local is null
            ? remote ?? fallback
            : remote is null ? local : SessionAggregateMerge.Fold(new[] { local, remote });
    }

    /// <summary>
    ///     Synchronous facade over <see cref="UpsertAsync"/> for sync call sites
    ///     (tests / non-async callers). Production request paths prefer
    ///     <see cref="UpsertAsync"/>. Safe here — no <c>SynchronizationContext</c>
    ///     in ASP.NET Core / xUnit, so this blocks the thread without deadlock.
    /// </summary>
    public SessionAggregate Upsert(SessionSample sample)
        => UpsertAsync(sample).GetAwaiter().GetResult();

    /// <summary>
    ///     Look up the current aggregate for a fingerprint on a site by
    ///     reconstructing it from the bounded session. Null when no session (or
    ///     no snapshot + no verdict) exists.
    /// </summary>
    public SessionAggregate? TryGet(string siteId, string fingerprintId)
    {
        if (!_sessions.TryGet(siteId, out var coordinator)
            || coordinator is null
            || !coordinator.TryGetSession(fingerprintId, out var session)
            || session is null)
            return null;

        var aggregate = SessionAggregateMolecule.FromSession(session, fingerprintId, siteId);
        if (aggregate is not null) return aggregate;

        // No aggregate snapshot yet, but a WBA verdict may have been recorded
        // before the first sample (SetWebBotAuthVerdict). Return a minimal stub
        // carrying the verdict so it survives to the next request in the window.
        var verdict = WebBotAuthVerdictMolecule.FromSession(session);
        return verdict is null ? null : VerdictStub(fingerprintId, siteId, verdict);
    }

    /// <summary>
    ///     Records the Web Bot Auth verdict for a fingerprint as a session signal
    ///     (public metadata only). Does not raise on <see cref="Changes"/> — a
    ///     verdict update is not a behavioural shift.
    /// </summary>
    /// <remarks>
    ///     Writes <b>synchronously</b> into the bounded session (not via the
    ///     fire-and-forget registry bridge): the verdict cache is a read-after-write
    ///     contract — the atom writes a verdict and the next request in the window
    ///     reads it back — so the write must land before the caller returns.
    /// </remarks>
    public void SetWebBotAuthVerdict(string siteId, string fingerprintId, WebBotAuthCachedVerdict? verdict)
    {
        if (verdict is null) return;

        var coordinator = _sessions.GetOrCreate(siteId);
        if (coordinator is null) return; // site cap reached — verdict simply not cached

        // GetOrCreateSessionAsync completes synchronously (sync session factory), so
        // GetAwaiter().GetResult() blocks no thread pool work and cannot deadlock —
        // no SynchronizationContext in ASP.NET Core / xUnit. Same rationale as Upsert.
        var session = coordinator.GetOrCreateSessionAsync(fingerprintId).GetAwaiter().GetResult();
        session.Contribute(new SessionContribution(
            RequestId: $"wba:{fingerprintId}:{verdict.KeyId}",
            At: DateTimeOffset.UtcNow,
            Signals: new[] { WebBotAuthVerdictMolecule.ToSignal(verdict) }));
    }

    /// <summary>
    ///     The session-idle sweep (operator 2026-08-15: "the distance since the last
    ///     request, that is the threshold"): finalizes every session whose LAST
    ///     contribution is older than <see cref="SessionStoreOptions.MaxIdle"/> — the
    ///     stopped/paused class. Independent of whether another request ever arrives;
    ///     driven on a cadence by the caller. The finalize is DELEGATED to the cache's
    ///     onEvict (the single finalize owner — the Lifecycle chain); the sweep only
    ///     invalidates the session + drops the shadow entry. Rig-visible Information
    ///     count.
    /// </summary>
    public async Task<int> FinalizeIdleSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_options.MaxIdle <= TimeSpan.Zero) return 0;

        var entries = _lastActivity.ToArray();
        var finalized = 0;
        foreach (var ((siteId, fingerprintId), lastActivity) in entries)
        {
            if (ct.IsCancellationRequested) break;
            if (now - lastActivity < _options.MaxIdle) continue;

            var coordinator = _sessions.TryGet(siteId, out var c) ? c : null;
            if (coordinator is null)
            {
                _lastActivity.TryRemove((siteId, fingerprintId), out _);
                continue;
            }

            if (!coordinator.TryGetSession(fingerprintId, out var session) || session is null)
            {
                _lastActivity.TryRemove((siteId, fingerprintId), out _);
                continue;
            }

            // The Lifecycle finalize is raised HERE (the cache's Invalidate is silent —
            // no onEvict fires, so there is no second finalize) and the session is
            // removed so its next upsert starts fresh.
            _lastActivity.TryRemove((siteId, fingerprintId), out _);
            RaiseFinalize(siteId, fingerprintId, session, SessionFinalizeReason.Ttl);
            coordinator.InvalidateSession(fingerprintId);
            finalized++;
        }

        if (finalized > 0)
        {
            _logger.LogInformation(
                "Session-idle sweep: {Count} session(s) idle past {MaxIdle:hh\\:mm\\:ss} finalized",
                finalized, _options.MaxIdle);
        }
        return finalized;
    }

    /// <summary>
    ///     Raises the Lifecycle finalize chain for an epoch's end — shared by the
    ///     max-lifetime chunk, the idle sweep, and the cache's own eviction
    ///     (OnSessionEvictedAsync). The finalize signal's payload carries the epoch's
    ///     aggregate projection (built from the session's latest contributions) + the
    ///     reason; the ack protocol (FinalizeDeadline / ExpectedAckCount) is the
    ///     subscribers' contract.
    /// </summary>
    private void RaiseFinalize(
        string siteId, string fingerprintId, Session session, SessionFinalizeReason reason)
    {
        var aggregate = SessionAggregateMolecule.FromSession(session, fingerprintId, siteId);
        if (aggregate is null) return;

        Lifecycle.Raise(
            signal: SessionFinalizingSignal.Key.Name,
            payload: new SessionFinalizingSignal
            {
                FingerprintId = fingerprintId,
                SiteId = siteId,
                Aggregate = aggregate,
                DeadlineUtc = DateTimeOffset.UtcNow + _options.FinalizeDeadline,
                Reason = reason,
            },
            key: fingerprintId);
    }

    private ValueTask OnSessionEvictedAsync(
        string siteId,
        string fingerprintId,
        Session session,
        CancellationToken ct)
    {
        // The cache has removed this value already. Build the final projection from
        // the frozen value supplied by onEvict rather than attempting a racy lookup.
        _lastActivity.TryRemove((siteId, fingerprintId), out _);
        RaiseFinalize(siteId, fingerprintId, session, SessionFinalizeReason.Ttl);
        return ValueTask.CompletedTask;
    }

    private static SessionAggregate VerdictStub(string fingerprintId, string siteId, WebBotAuthCachedVerdict verdict) =>
        new()
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            FirstSample = DateTimeOffset.UtcNow,
            LastSample = DateTimeOffset.UtcNow,
            SampleCount = 0,
            MeanBotProbability = 0.5,
            MaxBotProbability = 0.5,
            LatestConfidence = 0.0,
            HoneypotHits = 0,
            UpstreamStatusCounts = new Dictionary<int, int>(),
            DominantClientType = null,
            WebBotAuthVerdict = verdict,
            RetentionPriority = 0.0,
        };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_ownsRegistry) return;
        try { _sessions.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* best-effort dispose of the owned registry */ }
    }
}

/// <summary>
///     Named signals raised on <see cref="SessionStore.Changes"/>. Session atom
///     senses these to react to aggregate mutations.
/// </summary>
public static class SessionSignalKeys
{
    public static readonly SignalKey<SessionAggregate> AggregateUpdated =
        new("session.aggregate.updated");
    public static readonly SignalKey<SessionFoldObservation> Observation =
        new("session.observation");
}
