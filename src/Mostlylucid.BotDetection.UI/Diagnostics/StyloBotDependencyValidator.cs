using System.Reflection;

namespace Mostlylucid.BotDetection.UI.Diagnostics;

/// <summary>
///     Fail-fast validation of the dashboard's REQUIRED dependency-package
///     contract at composition time.
///     <para>
///         The <c>Mostlylucid.BotDetection.UI</c> nuspec hard-depends on the
///         packs listed in <see cref="RequiredPacks" /> (core detection + the
///         OpenApi pack that backs the routes tab). If a consumer's restore was
///         skew-clean but one of those assemblies is missing at runtime (deleted
///         file, version mismatch, hand-built bin), the dashboard would otherwise
///         fail much later with a cryptic <see cref="TypeLoadException" /> deep
///         in startup. This check surfaces that up front with an actionable
///         message naming the package and the exact install command.
///     </para>
///     <para>
///         PrometheusPack is deliberately NOT in the required set: it is an
///         optional add-on whose widget surface is registered by its own
///         <c>AddPrometheusPack</c>, and its absence degrades gracefully (no
///         meter-health tile) rather than failing boot. See the
///         dependency-inversion notes in <c>PrometheusPackServiceCollectionExtensions</c>.
///     </para>
/// </summary>
public static class StyloBotDependencyValidator
{
    /// <summary>
    ///     The assembly-name → package-id pairs the dashboard hard-depends on.
    ///     Order matters only for the first failure reported.
    /// </summary>
    public static readonly (string AssemblyName, string PackageId)[] RequiredPacks =
    {
        ("Mostlylucid.BotDetection", "Mostlylucid.BotDetection"),
        ("Mostlylucid.BotDetection.OpenApi", "Mostlylucid.BotDetection.OpenApi"),
    };

    /// <summary>
    ///     Verify every required pack assembly is present. Throws
    ///     <see cref="InvalidOperationException" /> naming the missing package
    ///     and its install command when one is absent.
    /// </summary>
    /// <param name="assemblyPresent">
    ///     Test seam: reports whether an assembly name is loadable. Defaults to
    ///     <see cref="Assembly.Load(AssemblyName)" />. Tests inject a stub to
    ///     simulate a missing package without corrupting the real load context.
    /// </param>
    public static void ValidateRequiredPacks(Func<string, bool>? assemblyPresent = null)
    {
        assemblyPresent ??= IsAssemblyPresent;

        foreach (var (assembly, package) in RequiredPacks)
        {
            if (!assemblyPresent(assembly))
                throw Missing(package, assembly);
        }
    }

    private static bool IsAssemblyPresent(string name)
    {
        try
        {
            Assembly.Load(new AssemblyName(name));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static InvalidOperationException Missing(string package, string assembly)
        => new(
            $"StyloBot dashboard requires the '{package}' package (assembly '{assembly}' is missing or " +
            $"could not be loaded). Add it with: dotnet add package {package}. " +
            "If you believe this is a version-skew issue, verify every Mostlylucid.BotDetection.* " +
            "package is pinned to the same version.");
}
