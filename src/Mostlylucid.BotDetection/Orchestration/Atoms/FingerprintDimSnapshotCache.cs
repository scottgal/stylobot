using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Dedicated per-fingerprint dim-snapshot cache for <see cref="IdentityChangeContributor"/>.
///     Singleton, in-memory only (per CLAUDE.md ConcurrentDictionary rule for transient
///     per-request state). Carries a <see cref="Reset"/> hook so the BDF rig and tests
///     can flush it alongside <c>SqliteFingerprintStore.TruncateAll</c> - otherwise
///     scenario N inherits scenario N-1's baselines and trips spurious risk.* signals.
///
///     <para>
///         Backed by the house <see cref="BoundedCache{TKey,TValue}"/> (active size-cap +
///         TTL eviction on every <c>Set</c>) rather than a raw <c>ConcurrentDictionary</c>.
///         The dictionary form only evicted lazily-per-key inside <c>Get</c>, so a rotated /
///         one-shot <c>fingerprintId</c> that was never read back before its 24h TTL lived
///         for the pod's whole lifetime. Under identity rotation that grew unbounded and
///         OOM-crashed the gateway roughly every 1-2h. The cap + TTL come from
///         <see cref="Models.IdentityOptions.FingerprintDimSnapshotCacheMaxSize"/> and
///         <see cref="Models.IdentityOptions.FingerprintDimSnapshotCacheTtl"/>.
///     </para>
/// </summary>
public sealed class FingerprintDimSnapshotCache
{
    public sealed record DimSnapshot(
        string Country,
        string Asn,
        string UaFamily,
        bool IsDatacenter,
        bool IsTorOrVpn,
        DateTimeOffset LastSeenUtc,
        // Bonus A: canvas + WebGL "shape" hash. Hardware-derived, effectively
        // immutable for real users; under the same fingerprint id, a change
        // is the canonical Multilogin / Kameleo profile-swap signal.
        string ShapeHash = "",
        // BotD verdict ("selenium", "puppeteer", "headless_chrome", ...).
        // Drift here means the automation framework changed under the same
        // identity, which is rare for legitimate operators.
        string BotdKind = "");

    private readonly BoundedCache<string, DimSnapshot> _snapshots;

    /// <param name="maxSize">
    ///     Hard ceiling on distinct fingerprint snapshots. Defaults to 50 000
    ///     (matches the <c>SqliteFingerprintStore</c> in-memory cap). Beyond this
    ///     the BoundedCache actively evicts the LFU / expired tail on each write.
    /// </param>
    /// <param name="ttl">Per-snapshot lifetime; defaults to 24h.</param>
    public FingerprintDimSnapshotCache(int maxSize = 50_000, TimeSpan? ttl = null)
    {
        _snapshots = new BoundedCache<string, DimSnapshot>(maxSize, ttl ?? TimeSpan.FromHours(24));
    }

    public DimSnapshot? Get(string fingerprintId)
        => _snapshots.TryGet(fingerprintId, out var snap) ? snap : null;

    public void Set(string fingerprintId, DimSnapshot snapshot)
        => _snapshots.Set(fingerprintId, snapshot);

    public void Reset() => _snapshots.Clear();

    public int Count => _snapshots.Count;
}
