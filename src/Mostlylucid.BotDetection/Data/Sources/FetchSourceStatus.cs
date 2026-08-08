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
///     One external fetch source's full picture: what it is, whether it's configured/enabled,
///     and — where the fetcher has been instrumented for it — whether it has ever actually
///     succeeded. This is the read model the admin UI and generated docs both project from;
///     never hand-maintain a parallel description of a source anywhere else.
/// </summary>
/// <param name="Id">Stable identifier, matches the owning fetcher's manifest/config key.</param>
/// <param name="DisplayName">Human-readable name for UI/docs.</param>
/// <param name="Url">Current effective URL, or null if this source has no single URL (e.g. an empty operator-supplied list).</param>
/// <param name="Enabled">Whether this source is currently configured to fetch.</param>
/// <param name="Purpose">What detection capability degrades without this source.</param>
/// <param name="Licence">Redistribution/attribution terms, or null if not applicable/known.</param>
/// <param name="Cadence">Human-readable refresh cadence and what drives it (e.g. "Tick1h, gated on 24h elapsed").</param>
/// <param name="FailureMode">Fail-open or fail-closed behavior.</param>
/// <param name="OnDiskLocation">Where the fetched data lands, or null if it's held in memory only / not applicable.</param>
/// <param name="LastSuccessUtc">
///     UTC timestamp of the last successful fetch this process has observed, or null if it has
///     never succeeded (or the fetcher isn't instrumented to know — see <see cref="HasLiveState"/>).
/// </param>
/// <param name="LastFailureUtc">UTC timestamp of the last failed fetch attempt, or null if none observed.</param>
/// <param name="HasLiveState">
///     False means this fetcher hasn't been instrumented to report last-success/last-failure yet —
///     <see cref="LastSuccessUtc"/>/<see cref="LastFailureUtc"/> being null here means "unknown", NOT
///     "never attempted". Never render this the same as a genuine never-fetched alarm; the two must
///     stay visually and textually distinct or the loud-alarm contract collapses into noise.
/// </param>
public sealed record FetchSourceStatus(
    string Id,
    string DisplayName,
    string? Url,
    bool Enabled,
    string Purpose,
    string? Licence,
    string Cadence,
    FetchFailureMode FailureMode,
    string? OnDiskLocation,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    bool HasLiveState);

/// <summary>
///     Implemented by whichever project owns a fetch source, registered as a normal DI service
///     alongside the fetcher itself — "every external fetch declares itself to one registry, at
///     the point it is registered in DI" (dl- mission). <see cref="IFetchSourceRegistry"/>
///     aggregates every registered contributor; it never hand-maintains its own source list.
/// </summary>
public interface IFetchSourceContributor
{
    /// <summary>Computed at read, never cached here — see <see cref="FetchSourceStatus"/> for what "live" means per field.</summary>
    IEnumerable<FetchSourceStatus> GetSources();
}

/// <summary>
///     The single source of truth for "what does this system fetch externally, and what's its
///     state" — every entry comes from a registered <see cref="IFetchSourceContributor"/>, never
///     a hand-maintained list here. Both the admin UI and generated docs read this and only this.
/// </summary>
public interface IFetchSourceRegistry
{
    IReadOnlyList<FetchSourceStatus> GetAll();
}

/// <summary>
///     Flattens every registered <see cref="IFetchSourceContributor"/>. Deliberately does no
///     caching of its own -- "never a cache, compute at read" (feedback_no_caches_freshness_over_locality)
///     -- each contributor already reads live Options/service state itself.
/// </summary>
internal sealed class FetchSourceRegistry : IFetchSourceRegistry
{
    private readonly IEnumerable<IFetchSourceContributor> _contributors;

    public FetchSourceRegistry(IEnumerable<IFetchSourceContributor> contributors)
    {
        _contributors = contributors;
    }

    public IReadOnlyList<FetchSourceStatus> GetAll()
        => _contributors.SelectMany(c => c.GetSources()).ToArray();
}
