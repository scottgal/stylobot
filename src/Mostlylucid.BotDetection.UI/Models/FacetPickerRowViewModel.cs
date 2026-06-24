namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One picker row inside <c>_FacetPicker.cshtml</c>. Three fields --
///     <see cref="Facet"/> matches an entry from
///     <see cref="Services.IFacetPickerCatalog"/>, <see cref="Op"/> is one of
///     the canonical predicate operator strings (eq / neq / gte / gt / lte /
///     lt / in / not_in / between / matches / contains), and
///     <see cref="Value"/> is the rendered string the per-type input control
///     re-hydrates (the picker UI maps booleans / enums / numbers / globs /
///     plain strings onto the right input shape from <see cref="Facet"/>'s
///     catalog entry).
///     <para>
///         The picker is structural only in B8 -- the picker writes nothing
///         to the predicate, and edits flow through the raw-DSL textarea
///         (which IS the canonical predicate source). Picker rows seed the
///         visible UI from the rule's existing AST when it parses cleanly
///         into a flat AND-of-Terms shape; mixed AND/OR predicates render an
///         empty picker and surface only via the textarea.
///     </para>
/// </summary>
/// <param name="Facet">SignalKeys value (e.g. <c>"ua.bot_type"</c>); matches a <see cref="Services.FacetPickerEntry.Facet"/>.</param>
/// <param name="Op">Canonical op key (e.g. <c>"eq"</c>, <c>"matches"</c>).</param>
/// <param name="Value">Value as a string; per-facet input control re-renders it.</param>
public sealed record FacetPickerRowViewModel(
    string Facet,
    string Op,
    string Value);
