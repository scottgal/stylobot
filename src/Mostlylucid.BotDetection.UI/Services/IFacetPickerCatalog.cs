namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Curated webmaster-facing facet picker entry. The 380+ raw
///     <c>SignalKeys</c> constants remain accessible via the raw text DSL on
///     expand; this picker narrows the menu to ~25 of the most-useful facets
///     with friendly labels, per-facet operator subsets, and value-type hints
///     the inline editor consumes to render the right input control.
/// </summary>
public sealed record FacetPickerEntry(
    string Label,
    string Facet,
    string Type,
    IReadOnlyList<string> Ops,
    IReadOnlyList<string> Values,
    FacetRange? Range,
    string Help,
    string? Category = null,
    string? Icon = null);

/// <summary>Slider range for <c>type: number</c> facets.</summary>
public sealed record FacetRange(double Min, double Max, double Step);

/// <summary>
///     Loads the curated facet picker catalog from embedded YAML
///     (<c>Definitions/PolicyFacets/picker-catalog.yaml</c>). The catalog is
///     immutable for the lifetime of the process; B8's inline editor reads
///     <see cref="All"/> for the dropdown and <see cref="FindByFacet"/> when
///     re-hydrating a saved rule into the picker UI.
/// </summary>
public interface IFacetPickerCatalog
{
    /// <summary>All curated facets, in YAML order (catalog also drives the dropdown order).</summary>
    IReadOnlyList<FacetPickerEntry> All { get; }

    /// <summary>Lookup by canonical facet key (e.g. <c>ua.bot_type</c>); ordinal-comparison.</summary>
    FacetPickerEntry? FindByFacet(string facet);
}
