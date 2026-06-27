using System.Collections.Concurrent;
using Mostlylucid.Atoms.Ephemeral;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     LFU-style picker for the cluster LLM-naming path. Replaces the
///     push-then-fire flow inside the legacy
///     <c>BotClusterDescriptionService.EnqueueClustersForDescriptionAsync</c>:
///     the caller (the existing ClustersUpdated event handler in EC6c) still pushes
///     every clustering wave through <see cref="TrackClusters"/>; the picker walks
///     the in-memory tracker each tick and surfaces clusters that still have no
///     description.
///
///     Source-of-truth for "needs description" mirrors the legacy filter verbatim:
///     <c>string.IsNullOrEmpty(cluster.Description)</c>. Members are kept on the
///     tracker entry so the prompter has the same payload the legacy
///     <c>LlmDescriptionCoordinator.ProcessClusterAsync</c> received.
///
///     Reservation against <see cref="ClusterInFlightSet"/> happens before return so
///     two concurrent ticks (or two pickers on the same atom) can't double-fire on
///     the same cluster id.
/// </summary>
public sealed class NeedsDescriptionClusterPicker : IEphemeralPicker<ClusterPickItem>
{
    private readonly ClusterInFlightSet _inFlight;

    // Tracker: cluster_id -> (latest cluster snapshot, members from that wave).
    // Push-state ported in from the legacy SignatureDescriptionService picker
    // pattern (EC6a). The legacy enqueue source was push-only via
    // BotClusterDescriptionService.OnClustersUpdated; that callsite re-points
    // onto TrackClusters in EC6c. The per-signature picker has since been
    // replaced by DriftTriggeredFingerprintPicker (LL1) but the cluster path
    // keeps the push tracker because cluster discovery is event-shaped.
    private readonly ConcurrentDictionary<string, ClusterTracker> _trackedClusters = new(StringComparer.Ordinal);

    public NeedsDescriptionClusterPicker(ClusterInFlightSet inFlight)
        => _inFlight = inFlight;

    /// <summary>
    ///     Push entry point for the cluster-update wave. Mirrors
    ///     <c>BotClusterDescriptionService.EnqueueClustersForDescriptionAsync</c>
    ///     verbatim: walks the wave, filters to clusters without descriptions,
    ///     materialises the per-cluster member list from the wave's behavior map,
    ///     and stores the latest snapshot. The threshold cross is no longer a
    ///     synchronous enqueue — the picker observes the cross on the next tick walk.
    /// </summary>
    public void TrackClusters(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors)
    {
        if (clusters.Count == 0) return;

        var behaviorMap = behaviors.ToDictionary(b => b.Signature);

        foreach (var cluster in clusters)
        {
            if (!string.IsNullOrEmpty(cluster.Description)) continue;

            var members = cluster.MemberSignatures
                .Where(s => behaviorMap.ContainsKey(s))
                .Select(s => behaviorMap[s])
                .ToList();

            if (members.Count == 0) continue;

            _trackedClusters[cluster.ClusterId] = new ClusterTracker(cluster.ClusterId, cluster, members);
        }
    }

    /// <summary>
    ///     Called by the writeback (or by the caller once a description is known)
    ///     to drop a cluster from the tracker so the picker stops surfacing it.
    /// </summary>
    public void Forget(string clusterId) => _trackedClusters.TryRemove(clusterId, out _);

    public IReadOnlyList<ClusterPickItem> Pick(int maxCount)
    {
        if (maxCount <= 0 || _trackedClusters.IsEmpty)
            return Array.Empty<ClusterPickItem>();

        var picks = new List<ClusterPickItem>(Math.Min(maxCount, 16));
        foreach (var kvp in _trackedClusters)
        {
            if (picks.Count >= maxCount) break;
            var tracker = kvp.Value;

            // Re-check the description hasn't filled in since we tracked it
            // (writeback may have raced ahead on another tick).
            if (!string.IsNullOrEmpty(tracker.Cluster.Description))
            {
                _trackedClusters.TryRemove(tracker.ClusterId, out _);
                continue;
            }

            if (!_inFlight.TryReserve(tracker.ClusterId)) continue;
            picks.Add(new ClusterPickItem(tracker.ClusterId, tracker.Cluster, tracker.Members));
        }
        return picks;
    }

    /// <summary>Diagnostics — total tracked clusters awaiting description.</summary>
    public int TrackedCount => _trackedClusters.Count;

    private sealed record ClusterTracker(
        string ClusterId,
        BotCluster Cluster,
        IReadOnlyList<SignatureBehavior> Members);
}
