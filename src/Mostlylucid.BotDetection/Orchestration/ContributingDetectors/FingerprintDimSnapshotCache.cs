using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

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
        DateTimeOffset LastSeenUtc);

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
