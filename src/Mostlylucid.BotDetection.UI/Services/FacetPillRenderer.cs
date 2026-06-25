namespace Mostlylucid.BotDetection.UI.Services;

public sealed record FacetPillModel(
    string Key,
    string CategoryLabel,
    string IconRef,
    string OperatorLabel,
    string ValueDisplay,
    string CategoryClass,
    bool IsUncatalogued);

public sealed class FacetPillRenderer
{
    private readonly IFacetPickerCatalog _catalog;

    public FacetPillRenderer(IFacetPickerCatalog catalog) => _catalog = catalog;

    public FacetPillModel Render(string facet, string op, string valueText)
    {
        var entry = _catalog.FindByFacet(facet);
        if (entry is null)
        {
            return new FacetPillModel(
                Key: facet,
                CategoryLabel: facet,
                IconRef: "tag",
                OperatorLabel: MapOp(op),
                ValueDisplay: valueText,
                CategoryClass: "facet-cat-uncatalogued",
                IsUncatalogued: true);
        }

        var catClass = "facet-cat-" + (entry.Category ?? "default").ToLowerInvariant();
        return new FacetPillModel(
            Key: facet,
            CategoryLabel: entry.Label,
            IconRef: entry.Icon ?? "tag",
            OperatorLabel: MapOp(op),
            ValueDisplay: valueText,
            CategoryClass: catClass,
            IsUncatalogued: false);
    }

    private static string MapOp(string op) => op switch
    {
        "eq" => "is",
        "ne" => "is not",
        "in" => "is one of",
        "gte" => "≥",
        "lte" => "≤",
        "matches" => "matches",
        _ => op,
    };
}
