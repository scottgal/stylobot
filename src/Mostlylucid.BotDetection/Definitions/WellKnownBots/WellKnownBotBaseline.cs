using System.Reflection;
using System.Text.Json;

namespace Mostlylucid.BotDetection.Definitions.WellKnownBots;

/// <summary>
///     The bundled arcjet well-known-bots snapshot, embedded in this assembly so
///     the <see cref="WellKnownBotIndex"/> is never empty before the first
///     successful download — cold start, an air-gapped host, or a failed/blocked
///     fetch all still get UA naming and classification.
///     <para>
///         <see cref="WellKnownBotRefreshService"/> seeds the index from this at
///         construction; a later successful download atomically upgrades it to the
///         live catalog. The snapshot carries only the fields the index consumes
///         (id / categories / pattern) so it stays small.
///     </para>
/// </summary>
public static class WellKnownBotBaseline
{
    // Suffix match rather than a hard-coded manifest name: MSBuild's resource
    // naming can mangle dotted/hyphenated file names, so resolve it defensively.
    private const string ResourceSuffix = "well-known-bots.baseline.json";

    /// <summary>
    ///     Loads the embedded baseline catalog. Returns an empty list if the
    ///     resource is missing or unreadable — the caller keeps whatever it had.
    /// </summary>
    public static IReadOnlyList<WellKnownBotEntry> Load()
    {
        var asm = typeof(WellKnownBotBaseline).Assembly;
        var name = Array.Find(
            asm.GetManifestResourceNames(),
            n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (name is null) return [];

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return [];

        // Source-generated JsonTypeInfo, not the reflection overload: reflection
        // JSON is disabled under PublishAot, so the reflection path would throw on
        // the AOT console/sidecar. See WellKnownBotsJsonContext.
        return JsonSerializer.Deserialize(stream, WellKnownBotsJsonContext.Default.ListWellKnownBotEntry)
               ?? [];
    }
}