using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionPackStep
{
    [YamlMember(Alias = "level")]
    public int Level { get; set; }

    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "policy")]
    public string Policy { get; set; } = string.Empty;

    [YamlMember(Alias = "activate")]
    public ReactionConditionSet? Activate { get; set; }

    [YamlMember(Alias = "deactivate")]
    public ReactionConditionSet? Deactivate { get; set; }
}
