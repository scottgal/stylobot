using YamlDotNet.Serialization;

namespace Mostlylucid.BotDetection.Models;

public sealed class ReactionPackDefinition
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = string.Empty;

    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; } = true;

    [YamlMember(Alias = "scope")]
    public string Scope { get; set; } = "global";

    [YamlMember(Alias = "priority")]
    public int Priority { get; set; }

    [YamlMember(Alias = "signals")]
    public List<string> Signals { get; set; } = [];

    [YamlMember(Alias = "steps")]
    public List<ReactionPackStep> Steps { get; set; } = [];

    public bool IsGlobal => string.Equals(Scope, "global", StringComparison.OrdinalIgnoreCase);

    public string? ScopedEndpoint => IsGlobal ? null : (Scope.Length > 9 ? Scope[9..] : null);
}
