using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     On-disk YAML shape of one <see cref="Template"/>. The file format is
///     human-friendly (snake_case keys, predicate + action as text) and is
///     parsed into <see cref="Template"/> by <see cref="YamlTemplateStore"/>.
/// </summary>
[YamlObject]
public sealed partial class YamlTemplateFile
{
    /// <summary>Stable template id (e.g. <c>protect-login</c>).</summary>
    [YamlMember("template")] public string Template { get; init; } = "";

    /// <summary>
    ///     Optional parent template id. When set, this template inherits the
    ///     parent's parameters and expansion entries: the child's parameters
    ///     are MERGED into the parent's (child wins on name match) and the
    ///     child's expansion entries are APPENDED to the parent's. T5 design.
    /// </summary>
    [YamlMember("extends")] public string? Extends { get; init; }

    /// <summary>Operator-facing label rendered in the template picker.</summary>
    [YamlMember("display-name")] public string DisplayName { get; init; } = "";

    /// <summary>One-line summary of operator intent.</summary>
    [YamlMember("description")] public string Description { get; init; } = "";

    /// <summary>
    ///     Grouping label for the template picker (e.g. <c>endpoint-protection</c>,
    ///     <c>overload</c>, <c>verified-bots</c>).
    /// </summary>
    [YamlMember("category")] public string Category { get; init; } = "";

    /// <summary>Declared parameters the template accepts. Optional; defaults to empty.</summary>
    [YamlMember("parameters")] public List<YamlTemplateParameter>? Parameters { get; init; }

    /// <summary>Ordered list of expansion entries -- one materialized rule per entry per applied-to scope.</summary>
    [YamlMember("expansion")] public List<YamlTemplateExpansionEntry>? Expansion { get; init; }
}

/// <summary>YAML representation of one <see cref="TemplateParameter"/>.</summary>
[YamlObject]
public sealed partial class YamlTemplateParameter
{
    /// <summary>Parameter name as it appears in the <c>{{ name }}</c> placeholder.</summary>
    [YamlMember("name")] public string Name { get; init; } = "";

    /// <summary>One of <c>int</c>, <c>number</c>, <c>string</c>, <c>duration</c>, <c>bool</c>.</summary>
    [YamlMember("type")] public string Type { get; init; } = "string";

    /// <summary>
    ///     Default value as a string. Resolution converts to the parameter's
    ///     declared type before formatting into the predicate / action text.
    ///     Optional -- absent means the application must supply the value.
    /// </summary>
    [YamlMember("default")] public string? Default { get; init; }

    /// <summary>Operator-facing description, rendered next to the editor input.</summary>
    [YamlMember("description")] public string? Description { get; init; }
}

/// <summary>YAML representation of one <see cref="TemplateExpansionEntry"/>.</summary>
[YamlObject]
public sealed partial class YamlTemplateExpansionEntry
{
    /// <summary>Identifier suffix -- unique within the template.</summary>
    [YamlMember("suffix")] public string Suffix { get; init; } = "";

    /// <summary>Predicate text in the expression language with <c>{{ placeholder }}</c> markers.</summary>
    [YamlMember("predicate")] public string Predicate { get; init; } = "";

    /// <summary>Action body in the compact flow shape with <c>{{ placeholder }}</c> markers.</summary>
    [YamlMember("action")] public string Action { get; init; } = "";

    /// <summary>Lower number = higher priority within the materialized rule's scope.</summary>
    [YamlMember("priority")] public int Priority { get; init; }

    /// <summary>One of <c>draft</c>, <c>observe</c>, <c>live</c>.</summary>
    [YamlMember("mode")] public string Mode { get; init; } = "live";
}
