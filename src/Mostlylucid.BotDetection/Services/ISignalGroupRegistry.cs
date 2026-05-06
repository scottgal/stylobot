namespace Mostlylucid.BotDetection.Services;

public interface ISignalGroupRegistry
{
    IReadOnlyList<string> Resolve(string groupReference);

    bool TryGetGroup(string groupName, out IReadOnlyList<string> signals);
}
