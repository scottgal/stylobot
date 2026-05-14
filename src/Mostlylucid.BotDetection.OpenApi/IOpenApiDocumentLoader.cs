namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     Loads and parses an OpenAPI document from a URL or local file path. Results are
///     cached in memory by source string so repeated calls are cheap.
///
///     Consumers:
///     - Dashboard Routes tab: cross-reference live routes against documented operations.
///     - API Holodeck: auto-generate honeypot responses for documented endpoints.
///     - Future: SDK generation, spec validation, doc drift detection.
/// </summary>
public interface IOpenApiDocumentLoader
{
    /// <summary>
    ///     Loads the document at <paramref name="source"/>. Returns a cached copy on
    ///     subsequent calls. <paramref name="source"/> may be:
    ///     - An absolute URL (http/https)
    ///     - An absolute or relative file path
    ///     Throws on parse failures unless <paramref name="continueOnFailure"/> is set;
    ///     in that case returns <c>null</c> and logs a warning.
    /// </summary>
    Task<LoadedOpenApiDocument?> LoadAsync(
        string source,
        bool continueOnFailure = false,
        CancellationToken ct = default);

    /// <summary>Clears the entire load cache. Called on config reload.</summary>
    void ClearCache();

    /// <summary>Removes a specific source from the cache. Returns true if anything was removed.</summary>
    bool Invalidate(string source);
}
