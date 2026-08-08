namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>How a fetcher behaves when the source is unreachable or the fetch fails.</summary>
public enum FetchFailureMode
{
    /// <summary>Keeps serving the last-good/embedded/bundled data; failure is silent unless something reads the diagnostic state.</summary>
    FailOpen,

    /// <summary>Refuses to start, or throws, when the source can't be reached (only when explicitly opted into).</summary>
    FailClosed
}

/// <summary>
///     A source's health, computed at read against its own declared cadence — never stored. "Healthy"
///     means fetched successfully within its own cadence, nothing weaker earns the word: a source
///     that succeeded once and then silently stopped ticking (exactly how the un-migrated
///     <c>ThreatIntelRefreshService</c> BackgroundService would fail — no errors, just nothing
///     happening) must not read as healthy forever just because <c>LastFailureUtc</c> stayed null.
/// </summary>
public enum FetchHealthState
{
    /// <summary>Not instrumented for live state (<see cref="FetchSourceStatus.HasLiveState"/> is false) — genuinely unknown, not a claim of health.</summary>
    Unknown,

    /// <summary>Never observed a successful OR failed fetch since this process started. Loud.</summary>
    NeverAttempted,

    /// <summary>Fetched successfully within its own declared cadence (× tolerance). The only state that earns "healthy".</summary>
    Healthy,

    /// <summary>Has succeeded before, but not recently enough relative to its declared cadence. Loud — this is the silently-stopped-ticking case.</summary>
    Stale,

    /// <summary>The most recent attempt (by timestamp) was a failure. Loud.</summary>
    Failing
}

/// <summary>
///     What a fetch source IS: static, synchronous, never reads a store. Declared by an
///     <see cref="IFetchSourceContributor"/> at DI-registration time. Deliberately carries no
///     last-success/last-failure data — that is <see cref="FetchSourceObservedState"/>'s job, read
///     from <see cref="IFetchSourceStateStore"/>, because it must survive a process restart and a
///     synchronous in-memory field cannot (see <see cref="FetchSourceStatus"/> for why that split
///     exists).
/// </summary>
/// <param name="Id">Stable identifier, matches the owning fetcher's manifest/config key AND the key it records observed state under.</param>
/// <param name="DisplayName">Human-readable name for UI/docs.</param>
/// <param name="Url">Current effective URL, or null if this source has no single URL (e.g. an empty operator-supplied list).</param>
/// <param name="Enabled">Whether this source is currently configured to fetch.</param>
/// <param name="Purpose">What detection capability degrades without this source.</param>
/// <param name="Licence">Redistribution/attribution terms, or null if not applicable/known.</param>
/// <param name="Cadence">Human-readable refresh cadence and what drives it (e.g. "Tick1h, gated on 24h elapsed").</param>
/// <param name="CadenceInterval">
///     The same cadence as an actual duration, or null if there isn't one meaningful to compute
///     staleness against (e.g. a manual/CLI-triggered source). Load-bearing, not decorative — this is
///     what <see cref="FetchSourceStatus.GetHealthState"/> measures "stale" against.
/// </param>
/// <param name="FailureMode">Fail-open or fail-closed behavior.</param>
/// <param name="OnDiskLocation">Where the fetched data lands, or null if it's held in memory only / not applicable.</param>
/// <param name="HasLiveState">
///     Whether this source's fetcher actually writes through <see cref="IFetchSourceStateStore"/>
///     and/or supplies <paramref name="DeriveLastSuccessUtc"/>. False means no observation will ever
///     appear for this id — render as "unknown", never the same as a genuine never-fetched alarm, or
///     the loud-alarm contract collapses into noise.
/// </param>
/// <param name="DeriveLastSuccessUtc">
///     Optional sync, cheap function computing "when did this source last actually succeed" directly
///     from the artefact it produces (e.g. a file's mtime), rather than a separately-persisted claim.
///     Preferred over <see cref="IFetchSourceStateStore"/>'s success tracking when present — the
///     artefact IS the evidence, so there is nothing to keep in sync and nothing to lose on restart:
///     even if storage is ephemeral, the artefact that exists after a restart (baked into the image,
///     or absent) truthfully reflects what has happened since. Only wire this for a source with a
///     genuinely exclusive, per-source artefact — a file/row shared across multiple declared sources
///     (e.g. botdetection.db backing 12 different DataSources entries) would give every one of them
///     the same wrong timestamp; leave this null for those and let <see cref="IFetchSourceStateStore"/>
///     (or nothing, today) carry success instead.
/// </param>
/// <param name="DeriveLastSuccessUtcAsync">
///     Same contract as <paramref name="DeriveLastSuccessUtc"/>, for evidence that can only be read
///     asynchronously (e.g. a DB query — <c>IBotListDatabase.GetLastUpdateTimeAsync</c>). Takes
///     precedence over <paramref name="DeriveLastSuccessUtc"/> when both are somehow set (they
///     shouldn't be). Overview-'s ruling (2026-08-08): when the evidence is shared by several
///     sources at bucket granularity, not per-source (e.g. <c>list_updates</c> has exactly two rows,
///     <c>bot_patterns</c>/<c>datacenter_ips</c>, covering 3 and 5 declared sources respectively),
///     do not wire this onto the individual sources — that would claim precision that does not
///     exist. Instead declare the bucket itself as its own source with this wired, and list what it
///     covers in <see cref="GroupedSourceIds"/>. Group-level truth beats per-source fiction.
/// </param>
/// <param name="GroupedSourceIds">
///     For a bucket-level source (see <paramref name="DeriveLastSuccessUtcAsync"/>), the ids of the
///     other declared sources this bucket's timestamp actually covers — lets the UI/docs render
///     "bot_patterns group — covering: isbot, matomo, crawler-user-agents" from data instead of a
///     hand-typed sentence. Null for an ordinary, non-bucket source.
/// </param>
public sealed record FetchSourceDeclaration(
    string Id,
    string DisplayName,
    string? Url,
    bool Enabled,
    string Purpose,
    string? Licence,
    string Cadence,
    TimeSpan? CadenceInterval,
    FetchFailureMode FailureMode,
    string? OnDiskLocation,
    bool HasLiveState,
    Func<DateTimeOffset?>? DeriveLastSuccessUtc = null,
    Func<CancellationToken, Task<DateTimeOffset?>>? DeriveLastSuccessUtcAsync = null,
    IReadOnlyList<string>? GroupedSourceIds = null);

/// <summary>
///     One external fetch source's full picture: a <see cref="FetchSourceDeclaration"/> joined with
///     its persisted <see cref="FetchSourceObservedState"/>. This is the read model the admin UI and
///     generated docs both project from; never hand-maintain a parallel description of a source
///     anywhere else. Built only by <see cref="IFetchSourceRegistry.GetAllAsync"/> — the join is why
///     that method is async while declaring a source stays synchronous.
/// </summary>
public sealed record FetchSourceStatus(
    string Id,
    string DisplayName,
    string? Url,
    bool Enabled,
    string Purpose,
    string? Licence,
    string Cadence,
    TimeSpan? CadenceInterval,
    FetchFailureMode FailureMode,
    string? OnDiskLocation,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    bool HasLiveState,
    IReadOnlyList<string>? GroupedSourceIds = null)
{
    /// <summary>Joins a declaration with its observed state, awaiting <see cref="FetchSourceDeclaration.DeriveLastSuccessUtcAsync"/> when the declaration supplies one.</summary>
    internal static async Task<FetchSourceStatus> JoinAsync(
        FetchSourceDeclaration declaration, FetchSourceObservedState? observed, CancellationToken ct)
    {
        var lastSuccess = declaration.DeriveLastSuccessUtcAsync is not null
            ? await declaration.DeriveLastSuccessUtcAsync(ct)
            : declaration.DeriveLastSuccessUtc is not null
                ? declaration.DeriveLastSuccessUtc()
                : observed?.LastSuccessUtc;

        return new FetchSourceStatus(
            declaration.Id, declaration.DisplayName, declaration.Url, declaration.Enabled,
            declaration.Purpose, declaration.Licence, declaration.Cadence, declaration.CadenceInterval,
            declaration.FailureMode, declaration.OnDiskLocation,
            lastSuccess, observed?.LastFailureUtc, declaration.HasLiveState, declaration.GroupedSourceIds);
    }

    /// <summary>
    ///     Computes <see cref="FetchHealthState"/> against <paramref name="now"/> — never stored,
    ///     always derived at read, same as everything else this registry reports. "Healthy" requires
    ///     a successful fetch within <see cref="CadenceInterval"/> × <paramref name="tolerance"/>, not
    ///     merely the absence of a recorded failure — see <see cref="FetchHealthState"/> for why.
    /// </summary>
    public FetchHealthState GetHealthState(DateTimeOffset now, double tolerance = 1.5)
    {
        if (!HasLiveState) return FetchHealthState.Unknown;
        if (LastSuccessUtc is null && LastFailureUtc is null) return FetchHealthState.NeverAttempted;

        // Most recent attempt (by timestamp) was a failure - loud regardless of cadence.
        if (LastFailureUtc is not null && (LastSuccessUtc is null || LastFailureUtc > LastSuccessUtc))
            return FetchHealthState.Failing;

        if (CadenceInterval is { } cadence && LastSuccessUtc is { } lastSuccess
            && now - lastSuccess > cadence * tolerance)
            return FetchHealthState.Stale;

        return FetchHealthState.Healthy;
    }
}

/// <summary>
///     Implemented by whichever project owns a fetch source, registered as a normal DI service
///     alongside the fetcher itself — "every external fetch declares itself to one registry, at
///     the point it is registered in DI" (dl- mission). <see cref="IFetchSourceRegistry"/>
///     aggregates every registered contributor; it never hand-maintains its own source list.
///     Synchronous and static on purpose — see <see cref="FetchSourceDeclaration"/>.
/// </summary>
public interface IFetchSourceContributor
{
    IEnumerable<FetchSourceDeclaration> GetSources();
}

/// <summary>
///     The single source of truth for "what does this system fetch externally, and what's its
///     state" — every entry comes from a registered <see cref="IFetchSourceContributor"/>, never
///     a hand-maintained list here. Both the admin UI and generated docs read this and only this.
/// </summary>
public interface IFetchSourceRegistry
{
    /// <summary>Every declared source, no observed state attached. Sync, cheap, safe to call often.</summary>
    IReadOnlyList<FetchSourceDeclaration> GetDeclarations();

    /// <summary>
    ///     Every declared source joined with its persisted observed state from
    ///     <see cref="IFetchSourceStateStore"/> — this is what the admin UI and generated docs use.
    ///     Async because the join reads a durable store, not because anything here is slow to compute.
    /// </summary>
    Task<IReadOnlyList<FetchSourceStatus>> GetAllAsync(CancellationToken ct = default);
}

/// <summary>
///     Flattens every registered <see cref="IFetchSourceContributor"/> and joins in
///     <see cref="IFetchSourceStateStore"/>'s persisted observations. Deliberately does no caching of
///     its own -- "never a cache, compute at read" (feedback_no_caches_freshness_over_locality) --
///     declarations are read live from Options/service state, and observed state is read live from
///     the store on every call.
/// </summary>
internal sealed class FetchSourceRegistry : IFetchSourceRegistry
{
    private readonly IEnumerable<IFetchSourceContributor> _contributors;
    private readonly IFetchSourceStateStore _stateStore;

    public FetchSourceRegistry(IEnumerable<IFetchSourceContributor> contributors, IFetchSourceStateStore stateStore)
    {
        _contributors = contributors;
        _stateStore = stateStore;
    }

    public IReadOnlyList<FetchSourceDeclaration> GetDeclarations()
        => _contributors.SelectMany(c => c.GetSources()).ToArray();

    public async Task<IReadOnlyList<FetchSourceStatus>> GetAllAsync(CancellationToken ct = default)
    {
        var declarations = GetDeclarations();
        var observed = await _stateStore.GetAllAsync(ct);

        var statuses = new FetchSourceStatus[declarations.Count];
        for (var i = 0; i < declarations.Count; i++)
            statuses[i] = await FetchSourceStatus.JoinAsync(declarations[i], observed.GetValueOrDefault(declarations[i].Id), ct);

        return statuses;
    }
}
