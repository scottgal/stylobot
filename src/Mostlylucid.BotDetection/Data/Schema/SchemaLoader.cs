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
    private const string CoreResourcePrefix = "Mostlylucid.BotDetection.Data.Schema.";

    /// <summary>
    ///     Returns the DDL text for the named schema from the core
    ///     <c>Mostlylucid.BotDetection.Data.Schema</c> namespace. Sugar over
    ///     <see cref="Load(Assembly, string, string)"/> for the common case;
    ///     downstream projects (UI / ApiHolodeck / Gateway) call the overload
    ///     with their own assembly + namespace prefix.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the embedded resource is missing -- typically a
    ///     forgotten <c>EmbeddedResource</c> Include in the <c>.csproj</c>.
    ///     Fail-fast at startup is better than a silent "no such table"
    ///     SQLite error six call stacks deep.
    /// </exception>
    public static string Load(string name)
        => Load(OwningAssembly, CoreResourcePrefix, name);

    /// <summary>
    ///     Generic overload: reads the embedded resource
    ///     <c>{resourcePrefix}{name}.sql</c> from <paramref name="assembly"/>.
    ///     Each downstream project gets a tiny facade
    ///     (e.g. <c>UiSchemaLoader.Load</c>) that pre-binds its assembly
    ///     and namespace prefix so call sites stay one-liners.
    /// </summary>
    public static string Load(Assembly assembly, string resourcePrefix, string name)
    {
        var cacheKey = assembly.FullName + "::" + resourcePrefix + name;
        return Cache.GetOrAdd(cacheKey, _ =>
        {
            var resourceName = resourcePrefix + name + ".sql";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded SQL schema '{resourceName}' not found in {assembly.GetName().Name}. " +
                    $"Check the .sql file exists and the .csproj " +
                    $"<EmbeddedResource Include=\"Data\\Schema\\*.sql\" /> is in place.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        });
    }
}
