namespace Mostlylucid.BotDetection.StyloExtract.ContentCache;

/// <summary>
///     Response representation a cached entry holds. Part of the cache key so the HTML
///     and Markdown variants can never cross-serve each other's payloads.
/// </summary>
public enum ContentRepresentation
{
    Html,
    Markdown,
}
