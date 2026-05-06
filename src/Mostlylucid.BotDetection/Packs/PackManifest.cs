using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Packs;

public sealed class PackManifest
{
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [YamlMember(Alias = "version")]
    public string Version { get; init; } = "1.0.0";

    [YamlMember(Alias = "description")]
    public string Description { get; init; } = "";

    [YamlMember(Alias = "author")]
    public string Author { get; init; } = "";

    [YamlMember(Alias = "requires_tier")]
    public string RequiresTier { get; init; } = "foss";

    [YamlMember(Alias = "min_core_version")]
    public string MinCoreVersion { get; init; } = "1.0.0";

    [YamlMember(Alias = "assembly")]
    public string? Assembly { get; init; }

    [YamlMember(Alias = "entry_type")]
    public string? EntryType { get; init; }
}
