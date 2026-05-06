using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionConditionSet
{
    [YamlMember(Alias = "condition")]
    public string Condition { get; set; } = "any";

    [YamlMember(Alias = "rules")]
    public List<ReactionRule> Rules { get; set; } = [];

    public bool IsAny => string.Equals(Condition, "any", StringComparison.OrdinalIgnoreCase);
    public bool IsAll => !IsAny;
}
