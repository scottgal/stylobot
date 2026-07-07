namespace Mostlylucid.BotDetection.WebBotAuth;

/// <summary>
///     In-memory, atomically-swapped implementation of <see cref="IPublicKeyRegistry"/>.
///     Same shape as <c>WellKnownBotIndex</c>: an immutable array is published on
///     each <see cref="Replace"/>; readers see a stable snapshot with no locking.
///     Taxonomy: Coordinator.
///     <para>
///         Two layers. <b>Manual</b> keys are operator-supplied (from
///         <c>PublicKeyRegistryOptions.ManualKeys</c>) and sticky across refreshes;
///         they win on a keyId collision. <b>Fetched</b> keys are swapped wholesale
///         on each successful refresh from the JSON manifest.
///     </para>
/// </summary>
public sealed class PublicKeyRegistry : IPublicKeyRegistry
{
    // Reference writes are atomic on all .NET platforms, so a reader always sees
    // one internally-consistent array without a lock (WellKnownBotIndex pattern).
    private volatile PublicKeyEntry[] _manual = [];
    private volatile PublicKeyEntry[] _fetched = [];
    private long _lastRefreshedTicks; // 0 == never; UTC ticks for lock-free reads

    /// <inheritdoc />
    public bool TryResolve(string keyId, out PublicKeyEntry entry)
    {
        // Manual first: operator keys override a fetched key with the same id.
        foreach (var e in _manual)
        {
            if (string.Equals(e.KeyId, keyId, StringComparison.Ordinal))
            {
                entry = e;
                return true;
            }
        }

        foreach (var e in _fetched)
        {
            if (string.Equals(e.KeyId, keyId, StringComparison.Ordinal))
            {
                entry = e;
                return true;
            }
        }

        entry = null!;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<PublicKeyEntry> Snapshot()
    {
        var manual = _manual;
        var fetched = _fetched;
        if (manual.Length == 0) return fetched;
        if (fetched.Length == 0) return manual;

        // Union with manual winning on keyId collisions.
        var result = new List<PublicKeyEntry>(manual.Length + fetched.Length);
        result.AddRange(manual);
        var manualIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in manual) manualIds.Add(e.KeyId);
        foreach (var e in fetched)
            if (!manualIds.Contains(e.KeyId))
                result.Add(e);
        return result;
    }

    /// <inheritdoc />
    public DateTimeOffset? LastRefreshedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastRefreshedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    ///     The fetched layer only (excludes config-seeded manual keys). This is
    ///     what the Escalator persists to the durable snapshot — manual keys come
    ///     from Options and are re-seeded on boot, so persisting them would be
    ///     redundant and could resurrect a removed manual key.
    /// </summary>
    public IReadOnlyList<PublicKeyEntry> FetchedSnapshot() => _fetched;

    /// <summary>Seeds the sticky operator-supplied manual key set (wins on keyId collisions).</summary>
    public void SeedManual(IReadOnlyList<PublicKeyEntry> manualKeys)
        => _manual = manualKeys as PublicKeyEntry[] ?? manualKeys.ToArray();

    /// <summary>Atomically replaces the fetched-key layer and records the refresh time.</summary>
    public void Replace(IReadOnlyList<PublicKeyEntry> fetched, DateTimeOffset refreshedUtc)
    {
        _fetched = fetched as PublicKeyEntry[] ?? fetched.ToArray();
        Interlocked.Exchange(ref _lastRefreshedTicks, refreshedUtc.UtcTicks);
    }
}
