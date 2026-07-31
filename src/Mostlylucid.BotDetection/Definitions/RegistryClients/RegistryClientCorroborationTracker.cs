using Mostlylucid.Ephemeral.Atoms.SlidingCache;

namespace Mostlylucid.BotDetection.Definitions.RegistryClients;

/// <summary>
///     Bounded, decaying, per-fingerprint record of "this identity recently proved
///     itself as a real registry client via corroborated OCI /v2/ protocol behaviour."
///     Lets a fingerprint's earned trust extend to a same-identity Harbor management-API
///     (/api/v2.0/*) call, where no safe UA-family signal exists on its own (see
///     <see cref="Orchestration.Atoms.RegistryClientSensor"/>).
/// </summary>
/// <remarks>
///     Trust is EARNED, never assumed: only <see cref="MarkCorroboratedAsync"/> can set
///     it, and only the sensor calls that -- exclusively after a real OCI corroboration
///     (never from a UA claim or from the /api/v2.0 request itself). The window is
///     short (a registry session, not persistent trust) and scoped strictly to the
///     fingerprint that earned it.
/// </remarks>
public interface IRegistryClientCorroborationTracker : IAsyncDisposable
{
    /// <summary>Records that <paramref name="fingerprint"/> was just corroborated by real OCI /v2/ behaviour.</summary>
    ValueTask MarkCorroboratedAsync(string fingerprint);

    /// <summary>True when <paramref name="fingerprint"/> was corroborated within the trust window.</summary>
    bool IsRecentlyCorroborated(string fingerprint);
}

/// <inheritdoc cref="IRegistryClientCorroborationTracker"/>
public sealed class RegistryClientCorroborationTracker : IRegistryClientCorroborationTracker
{
    private readonly SlidingCacheAtom<string, bool> _recent;

    /// <param name="slidingWindow">Renewed on each corroboration; expires when activity stops.</param>
    /// <param name="maxLifetime">Hard cap even under continuous activity (never persistent).</param>
    /// <param name="capacity">Bounded entry count (LFU eviction beyond this).</param>
    public RegistryClientCorroborationTracker(
        TimeSpan slidingWindow, TimeSpan maxLifetime, int capacity)
    {
        _recent = new SlidingCacheAtom<string, bool>(
            factory: static (_, _) => Task.FromResult(true),
            slidingExpiration: slidingWindow,
            absoluteExpiration: maxLifetime,
            maxSize: capacity);
    }

    public RegistryClientCorroborationTracker(RegistryClientCatalog? catalog = null)
        : this(
            TimeSpan.FromMinutes((catalog ?? RegistryClientCatalog.Default).CorroborationWindowMinutes),
            TimeSpan.FromMinutes((catalog ?? RegistryClientCatalog.Default).CorroborationMaxLifetimeMinutes),
            (catalog ?? RegistryClientCatalog.Default).CorroborationTrackerCapacity)
    {
    }

    public async ValueTask MarkCorroboratedAsync(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint)) return;
        await _recent.GetOrComputeAsync(fingerprint, CancellationToken.None).ConfigureAwait(false);
    }

    public bool IsRecentlyCorroborated(string fingerprint)
        => !string.IsNullOrEmpty(fingerprint) && _recent.TryGet(fingerprint, out _);

    public ValueTask DisposeAsync() => _recent.DisposeAsync();
}
