using System.Collections.Concurrent;
using System.Reflection;

namespace Mostlylucid.BotDetection.Data.Schema;

/// <summary>
///     Loads SQLite DDL from embedded <c>.sql</c> resource files. The
///     canonical home for schema definitions across the FOSS persistence
///     layer; replaces the inline <c>CREATE TABLE IF NOT EXISTS</c>
///     string-constants pattern that was scattered across every SQLite
///     store.
///     <para>
///     Why <c>.sql</c> files instead of C# string constants: the SQL is
///     diffable, lints in any editor, formats correctly, and the schema
///     intent is co-located with the storage layer rather than wedged
///     into a multi-hundred-line C# class. Embedded resources keep the
///     deployment story unchanged (no extra files to ship).
///     </para>
///     <para>
///     Files live under <c>Data/Schema/</c> at compile time and are
///     embedded by the <c>EmbeddedResource Include="Data\Schema\*.sql"</c>
///     directive in the <c>.csproj</c>. Resource names follow the .NET
///     convention: <c>Mostlylucid.BotDetection.Data.Schema.{name}.sql</c>.
///     </para>
/// </summary>
public static class SchemaLoader
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly Assembly OwningAssembly = typeof(SchemaLoader).Assembly;
    private const string ResourcePrefix = "Mostlylucid.BotDetection.Data.Schema.";

    /// <summary>
    ///     Returns the DDL text for the named schema. <paramref name="name"/>
    ///     is the file's base name (without the <c>.sql</c> extension) and is
    ///     case-sensitive to match the embedded-resource name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the embedded resource is missing -- typically a
    ///     forgotten <c>EmbeddedResource</c> Include in the <c>.csproj</c>.
    ///     Fail-fast at startup is better than a silent "no such table"
    ///     SQLite error six call stacks deep.
    /// </exception>
    public static string Load(string name)
    {
        return Cache.GetOrAdd(name, static n =>
        {
            var resourceName = ResourcePrefix + n + ".sql";
            using var stream = OwningAssembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded SQL schema '{resourceName}' not found. " +
                    $"Check Data/Schema/{n}.sql exists and the .csproj " +
                    $"<EmbeddedResource Include=\"Data\\Schema\\*.sql\" /> is in place.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
