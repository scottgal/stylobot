using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionRule
{
    [YamlMember(Alias = "signal")]
    public string? Signal { get; set; }

    [YamlMember(Alias = "signal_group")]
    public string? SignalGroup { get; set; }

    [YamlMember(Alias = "above")]
    public double? Above { get; set; }

    [YamlMember(Alias = "below")]
    public double? Below { get; set; }

    [YamlMember(Alias = "for_seconds")]
    public double ForSeconds { get; set; } = 60.0;

    [YamlMember(Alias = "group_condition")]
    public string GroupCondition { get; set; } = "any";
}
