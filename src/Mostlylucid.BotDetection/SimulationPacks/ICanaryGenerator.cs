namespace Mostlylucid.BotDetection.SimulationPacks;

/// <summary>
///     Generates deterministic canary values for beacon tracking.
///     Implemented by ApiHolodeck's BeaconCanaryGenerator.
/// </summary>
public interface ICanaryGenerator
{
    string Generate(string fingerprint, string path);
}
