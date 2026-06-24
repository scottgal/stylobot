using VYaml.Annotations;
using VYaml.Serialization;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Loads <c>Definitions/PolicyFacets/picker-catalog.yaml</c> from the FOSS
///     Mostlylucid.BotDetection assembly (embedded resource) and exposes the
///     curated facets as <see cref="FacetPickerEntry"/> records. The YAML
///     ships in FOSS so FOSS-only deployments still get the picker UX even
///     though a couple of entries (e.g. <c>org.lockdown</c>) only have
///     contributors writing them in commercial deployments.
/// </summary>
public sealed class FacetPickerCatalog : IFacetPickerCatalog
{
    private readonly Dictionary<string, FacetPickerEntry> _byFacet;

    public FacetPickerCatalog()
    {
        All = LoadFromEmbeddedResource();
        _byFacet = All.ToDictionary(e => e.Facet, StringComparer.Ordinal);
    }

    public IReadOnlyList<FacetPickerEntry> All { get; }

    public FacetPickerEntry? FindByFacet(string facet) =>
        _byFacet.TryGetValue(facet, out var e) ? e : null;

    private static IReadOnlyList<FacetPickerEntry> LoadFromEmbeddedResource()
    {
        // The YAML is embedded in the FOSS detection assembly, not the UI
        // assembly. Hop to the IdentityArchetypeRegistry's assembly to find it
        // -- IdentityArchetypeRegistry lives in Mostlylucid.BotDetection and
        // is reachable from here via the project reference.
        var asm = typeof(Identity.IdentityArchetypeRegistry).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("picker-catalog.yaml", StringComparison.Ordinal));
        if (name is null) return Array.Empty<FacetPickerEntry>();

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return Array.Empty<FacetPickerEntry>();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        var parsed = YamlSerializer.Deserialize<FacetCatalogYaml>(ms.ToArray());
        if (parsed?.Facets is null) return Array.Empty<FacetPickerEntry>();

        var entries = new List<FacetPickerEntry>(parsed.Facets.Count);
        foreach (var f in parsed.Facets)
        {
            if (string.IsNullOrWhiteSpace(f.Facet)) continue;
            if (string.IsNullOrWhiteSpace(f.Label)) continue;
            if (string.IsNullOrWhiteSpace(f.Type)) continue;

            entries.Add(new FacetPickerEntry(
                Label: f.Label!,
                Facet: f.Facet!,
                Type: f.Type!,
                Ops: (IReadOnlyList<string>?)f.Ops ?? Array.Empty<string>(),
                Values: (IReadOnlyList<string>?)f.Values ?? Array.Empty<string>(),
                Range: f.Range is null ? null : new FacetRange(f.Range.Min, f.Range.Max, f.Range.Step),
                Help: f.Help ?? string.Empty));
        }

        return entries;
    }
}

// === VYaml deserialisation shapes ==========================================
// Mirrors the YAML schema at Definitions/PolicyFacets/picker-catalog.yaml.
// SnakeCase naming mirrors the picker's authored field names (e.g. "not_in",
// "step").

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class FacetCatalogYaml
{
    public int Version { get; set; }
    public List<FacetEntryYaml>? Facets { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class FacetEntryYaml
{
    public string? Label { get; set; }
    public string? Facet { get; set; }
    public string? Type { get; set; }
    public List<string>? Ops { get; set; }
    public List<string>? Values { get; set; }
    public FacetRangeYaml? Range { get; set; }
    public string? Help { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public sealed partial class FacetRangeYaml
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; }
}
