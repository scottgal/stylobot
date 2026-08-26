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
        catch (Exception ex) when (ex is FileNotFoundException
                                   or BadImageFormatException
                                   or TypeLoadException
                                   or NotSupportedException
                                   or System.Security.SecurityException)
        {
            // Broad set: on a future trimmed / NativeAOT dashboard host, Assembly.Load can
            // surface different load failures than the classic FileNotFoundException. Any of
            // them means the required pack is not present and must fail fast with the
            // actionable message rather than an unhandled crash.
            return false;
        }
    }

    private static InvalidOperationException Missing(string package, string assembly)
        => new(
            $"StyloBot dashboard requires the '{package}' package (assembly '{assembly}' is missing " +
            $"or could not be loaded). Add it with: dotnet add package {package}. " +
            "This check detects a completely missing/unloadable required pack; it does not " +
            "verify version alignment across Mostlylucid.BotDetection.* packages -- if you " +
            "suspect a version mismatch, pin all of them to the same version.");
}
