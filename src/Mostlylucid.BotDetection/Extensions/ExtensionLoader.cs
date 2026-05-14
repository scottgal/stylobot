using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Extensions;

public static class ExtensionLoader
{
    /// <summary>
    ///     Loads any extension assemblies listed in <c>BotDetection:Extensions:AssemblyPaths</c>
    ///     and invokes <see cref="IBotDetectionExtension.Configure"/> on each discovered
    ///     extension type. Call AFTER <c>AddBotDetection()</c>/<c>AddStyloBot()</c> so
    ///     extensions can override <c>TryAdd</c> registrations (e.g. commercial pack
    ///     controllers replacing the FOSS null impl).
    /// </summary>
    public static IServiceCollection AddStyloBotExtensions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = new ExtensionOptions();
        configuration.GetSection("BotDetection:Extensions").Bind(options);
        if (options.AssemblyPaths.Count == 0) return services;

        var loggerFactory = services
            .Where(sd => sd.ServiceType == typeof(ILoggerFactory) && sd.ImplementationInstance is ILoggerFactory)
            .Select(sd => (ILoggerFactory)sd.ImplementationInstance!)
            .FirstOrDefault();
        var logger = loggerFactory?.CreateLogger("Mostlylucid.BotDetection.Extensions.ExtensionLoader");

        foreach (var rawPath in options.AssemblyPaths)
        {
            var path = ResolvePath(rawPath);
            try
            {
                var asm = Assembly.LoadFrom(path);
                foreach (var t in asm.GetExportedTypes())
                {
                    if (!typeof(IBotDetectionExtension).IsAssignableFrom(t) || t.IsAbstract || t.IsInterface)
                        continue;
                    if (t.GetConstructor(Type.EmptyTypes) is null) continue;

                    var ext = (IBotDetectionExtension)Activator.CreateInstance(t)!;
                    ext.Configure(services, configuration);
                    logger?.LogInformation(
                        "Loaded extension {Name} v{Version} from {Path}", ext.Name, ext.Version, path);
                }
            }
            catch (Exception ex)
            {
                if (!options.ContinueOnLoadFailure) throw;
                logger?.LogWarning(ex, "Failed to load extension from {Path}; continuing without it", path);
            }
        }

        return services;
    }

    private static string ResolvePath(string raw)
        => Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
}
