using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.HealthEndpoints;

/// <summary>
///     Singleton catalog built once at startup from <see cref="HealthEndpointOptions"/>.
///     Provides O(n) segment-boundary prefix matching for health / probe path recognition,
///     where n is the number of configured paths (typically 10).
/// </summary>
/// <remarks>
///     <para>
///         Matching uses <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/>
///         so that <c>/health</c> matches <c>/health/liveness</c> but NOT
///         <c>/healthcheck</c>. Comparison is always case-insensitive.
///     </para>
///     <para>
///         The catalog is immutable after construction; the configured paths are
///         compiled into a <see cref="PathString"/> array at build time.
///     </para>
/// </remarks>
public sealed class HealthEndpointCatalog
{
    private readonly PathString[] _prefixes;

    public HealthEndpointCatalog(IOptions<HealthEndpointOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefixes = options.Value.Paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p))
            .ToArray();
    }

    /// <summary>
    ///     Returns <see langword="true"/> when <paramref name="path"/> starts with
    ///     any configured health-probe prefix at a segment boundary
    ///     (case-insensitive).
    /// </summary>
    public bool IsHealthPath(PathString path)
    {
        foreach (var prefix in _prefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
