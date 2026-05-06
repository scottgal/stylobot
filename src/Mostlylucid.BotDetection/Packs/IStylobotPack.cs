using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Packs;

public interface IStylobotPack
{
    string Name { get; }
    string Version { get; }
    void ConfigureServices(IServiceCollection services, IPackCapabilities capabilities);
}
