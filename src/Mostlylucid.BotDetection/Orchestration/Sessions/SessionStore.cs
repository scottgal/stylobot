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
    private readonly bool _ownsRegistry;
    private int _disposed;

    /// <summary>
    ///     Change-stream sink. Every upsert raises the freshly-merged aggregate.
    ///     The session atom hooks <c>TypedSignalRaised</c> to react to mutations.
    /// </summary>
    public TypedSignalSink<SessionAggregate> Changes { get; }

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

        const int sinkCap = 4096;
        Changes = new TypedSignalSink<SessionAggregate>(
            new SignalSink(maxCapacity: sinkCap, maxAge: options.Value.Ttl),
            maxCapacity: sinkCap, maxAge: options.Value.Ttl);
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
            options.Value.Ttl);
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
            return transient;
        }

        var session = await coordinator.GetOrCreateSessionAsync(sample.FingerprintId, ct).ConfigureAwait(false);

        var previous = SessionAggregateMolecule.FromSession(session, sample.FingerprintId, sample.SiteId);
        var merged = previous is null
            ? SessionAggregateMerge.FromFirstSample(sample)
            : SessionAggregateMerge.Merge(previous, sample);

        session.Contribute(new SessionContribution(
            RequestId: sample.RequestId ?? $"{sample.FingerprintId}:{merged.SampleCount}",
            At: sample.Timestamp,
            Signals: new[] { SessionAggregateMolecule.ToSignal(merged) }));

        Changes.Raise(SessionSignalKeys.AggregateUpdated.Name, merged, sample.FingerprintId);
        return merged;
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
}
