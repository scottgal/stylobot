namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Fast reverse lookup: signature -> the cluster (if any) it currently belongs to.
///     Walking <see cref="BotClusterService.GetClustersAsync"/> on every read would be
///     N members * N clusters; this exposes the in-memory dict the cluster service
///     already rebuilds on each <c>ClustersUpdated</c> event.
///     <para>
///     Used by <c>SignatureRiskInputsBuilder</c> to populate the cluster inputs on the
///     risk verdict for every signature the dashboard renders. Optional in remote-mode
///     hosts that don't run clustering -- the verdict composer handles a null cluster
///     gracefully.
///     </para>
/// </summary>
public interface IClusterMembershipLookup
{
    /// <summary>
    ///     The cluster the signature belongs to in the most-recent snapshot, or null
    ///     when the signature isn't a member of any cluster (single-actor traffic, or
    ///     the snapshot hasn't been computed yet).
    /// </summary>
    BotCluster? TryGetClusterForSignature(string primarySignature);
}
