using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.StyloWall.Middleware;
using Mostlylucid.StyloWall.Models;

namespace Mostlylucid.StyloWall.Extensions;

public static class StyloWallServiceCollectionExtensions
{
    public static IServiceCollection AddStyloWall(
        this IServiceCollection services,
        Action<StyloWallOptions>? configure = null)
    {
        services.AddOptions<StyloWallOptions>().BindConfiguration(StyloWallOptions.SectionName);
        if (configure is not null) services.Configure(configure);
        services.AddSingleton<StyloWallGate>();
        return services;
    }

    public static IServiceCollection AddStyloWall(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<StyloWallOptions>? configure = null)
    {
        services.AddOptions<StyloWallOptions>()
            .Bind(configuration.GetSection(StyloWallOptions.SectionName));
        if (configure is not null) services.Configure(configure);
        services.AddSingleton<StyloWallGate>();
        return services;
    }
}
