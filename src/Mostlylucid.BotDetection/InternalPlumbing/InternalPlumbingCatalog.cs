using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.InternalPlumbing;

/// <summary>
///     Singleton catalog built once at startup from <see cref="InternalPlumbingOptions"/>.
///     Provides O(n) segment-boundary prefix matching for the product's own plumbing paths
///     (the dashboard hub + the fingerprint beacon), where n is the number of configured
///     paths (typically 3).
/// </summary>
/// <remarks>
///     <para>
///         Matching uses <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/>
///         so that <c>/stylobot/hub</c> matches <c>/stylobot/hub/negotiate</c> but NOT
///         <c>/stylobot/hubspot</c>. Comparison is always case-insensitive.
///     </para>
///     <para>
///         The catalog is immutable after construction; the configured paths are
///         compiled into a <see cref="PathString"/> array at build time.
///     </para>
/// </remarks>
public sealed class InternalPlumbingCatalog
{
    private readonly PathString[] _prefixes;

    public InternalPlumbingCatalog(IOptions<InternalPlumbingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefixes = options.Value.Paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p))
            .ToArray();
    }

    /// <summary>
    ///     Returns <see langword="true"/> when <paramref name="path"/> starts with any
    ///     configured internal-plumbing prefix at a segment boundary (case-insensitive).
    /// </summary>
    public bool IsInternalPlumbingPath(PathString path)
    {
        foreach (var prefix in _prefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
