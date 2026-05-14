namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     In-memory catalog of operations from every OpenAPI document seeded at startup.
///     Indexed by route key ("GET:/users/{id}") so consumers (route catalog, honeypot,
///     policy seeder) can do O(1) lookups by HTTP method + path template.
/// </summary>
public interface IOpenApiCatalog
{
    /// <summary>True once the startup seeder has finished its initial population pass.</summary>
    bool IsSeeded { get; }

    /// <summary>Documents that were successfully loaded at startup.</summary>
    IReadOnlyList<LoadedOpenApiDocument> Documents { get; }

    /// <summary>
    ///     Total operation count across all loaded documents (after deduplication by
    ///     route key -last writer wins).
    /// </summary>
    int OperationCount { get; }

    /// <summary>
    ///     Try to find a documented operation by its route key
    ///     ("METHOD:/template", method uppercased). Returns null if not present.
    /// </summary>
    LoadedOpenApiOperation? TryGetByRouteKey(string routeKey);

    /// <summary>Enumerates every documented operation, sorted by route key.</summary>
    IReadOnlyList<LoadedOpenApiOperation> AllOperations();

    /// <summary>
    ///     Called by the startup seeder after loading documents. Replaces the catalog
    ///     contents atomically. Subsequent reads see the new state.
    /// </summary>
    void Replace(IReadOnlyList<LoadedOpenApiDocument> documents);
}
