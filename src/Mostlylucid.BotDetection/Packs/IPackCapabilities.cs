namespace Mostlylucid.BotDetection.Packs;

public interface IPackCapabilities
{
    bool CanWrite { get; }
    string Tier { get; }
}
