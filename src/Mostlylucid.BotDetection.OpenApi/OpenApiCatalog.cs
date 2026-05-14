namespace Mostlylucid.BotDetection.OpenApi;

public sealed class OpenApiCatalog : IOpenApiCatalog
{
    private IReadOnlyList<LoadedOpenApiDocument> _documents = Array.Empty<LoadedOpenApiDocument>();
    private IReadOnlyDictionary<string, LoadedOpenApiOperation> _byRouteKey
        = new Dictionary<string, LoadedOpenApiOperation>();
    private IReadOnlyList<LoadedOpenApiOperation> _allOps = Array.Empty<LoadedOpenApiOperation>();
    private bool _seeded;

    public bool IsSeeded => _seeded;
    public IReadOnlyList<LoadedOpenApiDocument> Documents => _documents;
    public int OperationCount => _byRouteKey.Count;

    public LoadedOpenApiOperation? TryGetByRouteKey(string routeKey)
        => _byRouteKey.TryGetValue(routeKey, out var op) ? op : null;

    public IReadOnlyList<LoadedOpenApiOperation> AllOperations() => _allOps;

    public void Replace(IReadOnlyList<LoadedOpenApiDocument> documents)
    {
        var lookup = new Dictionary<string, LoadedOpenApiOperation>(StringComparer.Ordinal);
        foreach (var doc in documents)
        foreach (var op in doc.Operations)
            lookup[$"{op.Method.ToUpperInvariant()}:{op.Path}"] = op;

        _documents = documents;
        _byRouteKey = lookup;
        _allOps = lookup.Values.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method).ToList();
        _seeded = true;
    }
}
