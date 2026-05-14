using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Implemented by a commercial (or third-party) extension assembly. The host calls
///     <see cref="Configure"/> during DI setup to let the extension register its services.
///     Drop the assembly DLL next to the host, list its path in
///     <c>BotDetection:Extensions:AssemblyPaths</c>, restart.
///
///     Implementations must be parameterless-constructable (the loader uses
///     <see cref="System.Activator.CreateInstance(Type)"/>); save state on the service
///     collection, not on the extension type.
/// </summary>
public interface IBotDetectionExtension
{
    string Name { get; }
    Version Version { get; }
    void Configure(IServiceCollection services, IConfiguration configuration);
}
