using Microsoft.AspNetCore.Builder;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Endpoint metadata marker that opts an endpoint OUT of the
///     <see cref="Policies.PolicyRegistry"/> file-extension static-asset
///     classification.
///
///     <para>
///     Background: <see cref="Policies.PolicyRegistry.GetPolicyForPath"/>
///     consults a list of static-asset file extensions (.css, .png, .xml,
///     .txt, ...) before any other path matching. A request whose path ends
///     in one of those extensions is assigned the <c>Static</c> detection
///     policy which runs minimal detection and produces a neutral verdict.
///     That is correct for actual static files served by
///     <c>UseStaticFiles</c>, but it silently breaks dynamic endpoints that
///     happen to share the same extension (e.g. <c>/sitemap.xml</c> wired
///     via <c>MapStyloBotSitemap</c>, or any dynamic <c>.xml</c> /
///     <c>.json</c> endpoint a host writes).
///     </para>
///
///     <para>
///     Endpoints carrying this metadata are recognised by the detection
///     middleware before it consults the registry, and the middleware skips
///     the static-asset shortcut for them. Apply via
///     <c>endpoints.MapGet(...).WithMetadata(NotStaticAssetMarker.Instance)</c>
///     or the <c>WithNotStaticAsset()</c> extension below.
///     </para>
/// </summary>
public sealed class NotStaticAssetMarker
{
    /// <summary>Singleton instance to attach as endpoint metadata.</summary>
    public static readonly NotStaticAssetMarker Instance = new();

    private NotStaticAssetMarker() { }
}

/// <summary>
///     Convenience extensions for marking endpoints as dynamic so the
///     detection pipeline does not classify them as static assets.
/// </summary>
public static class NotStaticAssetExtensions
{
    /// <summary>
    ///     Marks the endpoint so the detection middleware does not apply
    ///     the file-extension static-asset shortcut. Use for dynamic
    ///     endpoints whose path matches a static-asset extension
    ///     (sitemap.xml, robots.txt, custom .json APIs, etc.).
    /// </summary>
    public static TBuilder WithNotStaticAsset<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(b => b.Metadata.Add(NotStaticAssetMarker.Instance));
        return builder;
    }
}