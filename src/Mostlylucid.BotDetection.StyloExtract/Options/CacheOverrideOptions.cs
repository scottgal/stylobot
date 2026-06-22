namespace Mostlylucid.BotDetection.StyloExtract.Options;

/// <summary>
/// Cache-Control directives applied by the extract action policies.
/// Behaviour is gated by <see cref="Mode"/>:
///   Respect - no changes applied;
///   Override - replace Cache-Control entirely;
///   Add      - fill in only the missing directives.
/// </summary>
public sealed class CacheOverrideOptions
{
    /// <summary>How Cache-Control is modified. Default: Respect.</summary>
    public CacheControlMode Mode { get; set; } = CacheControlMode.Respect;

    /// <summary>Maps to <c>max-age=N</c>. Null means the directive is omitted.</summary>
    public int? MaxAge { get; set; }

    /// <summary>When true adds the <c>public</c> directive.</summary>
    public bool? Public { get; set; }

    /// <summary>When true adds <c>no-store</c>.</summary>
    public bool? NoStore { get; set; }

    /// <summary>When true adds <c>must-revalidate</c>.</summary>
    public bool? MustRevalidate { get; set; }

    /// <summary>
    /// When true, <c>X-StyloBot-BotType</c> is appended to the response Vary header.
    /// Useful for CDNs that can serve different cached variants per bot type.
    /// </summary>
    public bool VaryByBotType { get; set; }

    /// <summary>
    /// When true, <c>Accept</c> is appended to the response Vary header so downstream
    /// caches keep HTML and Markdown responses in separate buckets.
    /// </summary>
    public bool VaryByAccept { get; set; }
}
