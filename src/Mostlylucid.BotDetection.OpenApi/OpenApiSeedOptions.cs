namespace Mostlylucid.BotDetection.OpenApi;

/// <summary>
///     Startup configuration for the OpenAPI seeder. Bound from
///     <c>BotDetection:OpenApi</c> in appsettings. Empty list = disabled.
/// </summary>
public sealed class OpenApiSeedOptions
{
    /// <summary>
    ///     OpenAPI document sources to load on startup. Each entry is an absolute URL
    ///     (http/https) or a file path. Documents are loaded once at host start and
    ///     populated into <see cref="IOpenApiCatalog"/>. Add <c>SourceUrl</c> (singular)
    ///     for convenience -bound into the same list.
    /// </summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>
    ///     Singular convenience for the common single-document case. Merged into
    ///     <see cref="Sources"/> at bind time.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    ///     When true (default), startup load failures log a warning and the host
    ///     continues with an empty OpenAPI catalog. When false, failures throw and
    ///     prevent the host from starting.
    /// </summary>
    public bool ContinueOnFailure { get; set; } = true;

    /// <summary>
    ///     Returns the effective list of sources, merging <see cref="SourceUrl"/> into
    ///     <see cref="Sources"/> and de-duplicating.
    /// </summary>
    public IReadOnlyList<string> GetEffectiveSources()
    {
        var combined = new List<string>(Sources);
        if (!string.IsNullOrWhiteSpace(SourceUrl) && !combined.Contains(SourceUrl))
            combined.Add(SourceUrl);
        return combined;
    }
}
