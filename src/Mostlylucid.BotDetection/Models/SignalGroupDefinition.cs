using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class SignalGroupDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "signals")]
    public List<string> Signals { get; set; } = [];
}
