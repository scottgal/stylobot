using VYaml.Annotations;

namespace Mostlylucid.BotDetection.Policies.Signals;

/// <summary>
///     One overlay entry per YAML file under <c>Policies/Signals/Overlays/*.yaml</c>.
///     Each file describes one signal key and supplies whatever the reflection +
///     XML-doc pipeline cannot infer on its own: a constrained operator list, a
///     value domain (for enum-typed signals), worked examples, and cross-reference
///     hints to related signals. Every field is optional -- the loader merges in
///     only what the overlay supplies and falls back to sensible defaults otherwise.
///
///     <para>
///     Naming uses snake_case to match every other YAML surface in this assembly
///     (compliance packs, identity archetypes, simulation packs, etc.); <see cref="YamlMemberAttribute"/>
///     pins each field explicitly so the file format does not silently shift if a
///     property gets renamed in code.
///     </para>
/// </summary>
[YamlObject]
public sealed partial class SignalOverlay
{
    /// <summary>The signal key this overlay enriches (e.g. <c>score.bot_probability</c>).</summary>
    [YamlMember("key")] public string Key { get; init; } = "";

    /// <summary>
    ///     Operator literals (as they should appear in the expression editor) valid
    ///     for this signal. When set, this REPLACES the default-from-Kind list;
    ///     when null/empty the catalogue falls back to <see cref="SignalKind"/>'s
    ///     defaults.
    /// </summary>
    [YamlMember("operators")] public string[]? Operators { get; init; }

    /// <summary>Legacy alias for <see cref="Kind"/>. Kept so older overlay files keep loading.</summary>
    [YamlMember("value_kind")] public string? ValueKind { get; init; }

    /// <summary>
    ///     Override for the reflected <see cref="SignalKind"/>. Accepts the enum
    ///     name verbatim (<c>String</c>, <c>Bool</c>, <c>Number</c>, <c>EnumOf</c>,
    ///     <c>Array</c>). Set this to <c>EnumOf</c> when supplying <see cref="Values"/>.
    /// </summary>
    [YamlMember("kind")] public string? Kind { get; init; }

    /// <summary>Worked predicate snippets the editor can insert when the user picks this signal.</summary>
    [YamlMember("examples")] public SignalOverlayExample[]? Examples { get; init; }

    /// <summary>Cross-reference hints -- other signal keys the editor should surface alongside this one.</summary>
    [YamlMember("related")] public string[]? Related { get; init; }

    /// <summary>
    ///     For <see cref="SignalKind.EnumOf"/> signals: the closed set of acceptable
    ///     values. The editor uses this to drive value autocomplete after the operator.
    /// </summary>
    [YamlMember("values")] public string[]? Values { get; init; }
}

/// <summary>
///     Worked example pair carried by <see cref="SignalOverlay"/>. <see cref="Expr"/>
///     is the literal predicate as it should appear in the editor; <see cref="Reads"/>
///     is the plain-English one-liner shown alongside.
/// </summary>
[YamlObject]
public sealed partial class SignalOverlayExample
{
    /// <summary>The predicate literal (e.g. <c>score.bot_probability &gt;= 0.7</c>).</summary>
    [YamlMember("expr")] public string Expr { get; init; } = "";

    /// <summary>Plain-English read of <see cref="Expr"/>.</summary>
    [YamlMember("reads")] public string Reads { get; init; } = "";
}
