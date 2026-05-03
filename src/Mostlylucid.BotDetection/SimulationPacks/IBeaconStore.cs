namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Stores beacon canary to fingerprint mappings.
///     Implemented by ApiHolodeck's BeaconStore.
/// </summary>
public interface IBeaconStore
{
    Task StoreAsync(string canary, string fingerprint, string path, string? packId, TimeSpan ttl);
}
