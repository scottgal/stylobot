using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Dedicated per-fingerprint dim-snapshot cache for <see cref="IdentityChangeContributor"/>.
///     Singleton, in-memory only (per CLAUDE.md ConcurrentDictionary rule for transient
///     per-request state). Carries a <see cref="Reset"/> hook so the BDF rig and tests
///     can flush it alongside <c>SqliteFingerprintStore.TruncateAll</c> - otherwise
///     scenario N inherits scenario N-1's baselines and trips spurious risk.* signals.
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

    private readonly ConcurrentDictionary<string, DimSnapshot> _snapshots = new(StringComparer.Ordinal);
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromHours(24);

    public DimSnapshot? Get(string fingerprintId)
    {
        if (!_snapshots.TryGetValue(fingerprintId, out var snap)) return null;
        if (DateTimeOffset.UtcNow - snap.LastSeenUtc > SnapshotTtl)
        {
            _snapshots.TryRemove(fingerprintId, out _);
            return null;
        }
        return snap;
    }

    public void Set(string fingerprintId, DimSnapshot snapshot)
        => _snapshots[fingerprintId] = snapshot;

    public void Reset() => _snapshots.Clear();

    public int Count => _snapshots.Count;
}
