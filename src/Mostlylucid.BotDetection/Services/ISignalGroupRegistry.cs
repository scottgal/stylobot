namespace Mostlylucid.BotDetection.Services;

public interface ISignalGroupRegistry
{
    /// <summary>
    /// Resolves a "$group-name" reference to its signal keys.
    /// Returns empty if the argument does not start with '$' or the group is not found.
    /// </summary>
    IReadOnlyList<string> Resolve(string groupReference);

    bool TryGetGroup(string groupName, out IReadOnlyList<string> signals);
}
