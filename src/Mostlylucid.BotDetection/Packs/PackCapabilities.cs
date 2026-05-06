namespace Mostlylucid.BotDetection.Packs;

public sealed class PackCapabilities(bool canWrite) : IPackCapabilities
{
    public static readonly IPackCapabilities Foss = new PackCapabilities(false);
    public static readonly IPackCapabilities Commercial = new PackCapabilities(true);

    public bool CanWrite { get; } = canWrite;
    public string Tier => CanWrite ? "commercial" : "foss";
}
