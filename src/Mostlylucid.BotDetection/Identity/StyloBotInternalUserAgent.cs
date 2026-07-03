using System.Reflection;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Canonical User-Agent for stylobot's own internal HTTP clients
///     (marketing site -> gateway dashboard pull, AspNetPack remote sink/
///     query/hub, OtelMesh remote, Prometheus scraper, future gateway-to-
///     gateway calls).
///     <para>
///         Internal clients used to ship NO <c>User-Agent</c> header at
///         all -- the .NET default <c>HttpClient</c> sends none unless one
///         is set explicitly. The detection pipeline's identity archetypes
///         landed on the wget signature for empty-UA + repeating internal
///         HTTP/2, which is correct for an untyped CLI tool but wrong for
///         our own services. The fix isn't a detection-pipeline exemption
///         -- it is to make internal clients identify themselves the same
///         way real Prometheus / curl / Googlebot do, and have the
///         pipeline classify the request shape accurately.
///     </para>
///     <para>
///         Pair with the <c>stylobot-internal</c> identity archetype in
///         <c>Definitions/IdentityArchetypes/stylobot-internal.yaml</c>:
///         that archetype is what makes the pipeline recognise this UA
///         prefix as a known-internal client. The UA carries the contact
///         URL convention real crawlers use so operators can attribute
///         traffic upstream.
///     </para>
///     <para>
///         Format: <c>StyloBot.Internal/{version} (+https://stylo.bot)</c>.
///         The version is read from the FOSS assembly so a single source
///         of truth (the assembly's <see cref="AssemblyName.Version"/>)
///         drives every internal client across both repos.
///     </para>
/// </summary>
public static class StyloBotInternalUserAgent
{
    /// <summary>
    ///     Pre-formatted UA string. Computed once at class-load time and
    ///     reused across every <c>HttpClient</c> registration so all internal
    ///     callers ship the identical token -- the archetype matcher keys on
    ///     the prefix and any drift would defeat the classification.
    /// </summary>
    public static readonly string Value = Build();

    private static string Build()
    {
        var asmName = typeof(StyloBotInternalUserAgent).Assembly.GetName();
        var version = asmName.Version?.ToString(3) ?? "0.0.0";
        return $"StyloBot.Internal/{version} (+https://stylo.bot)";
    }
}