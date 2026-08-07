using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mostlylucid.SignalShingle.AspNetCore;

public static class SignalShingleExtensions
{
    /// <summary>Registers the string/HTML cache, SignalR beacon hub and tag-helper services.</summary>
    public static IServiceCollection AddSignalShingleUi(this IServiceCollection services,
        Action<SignalShingleCacheOptions>? configureCache = null,
        Action<SignalShingleUiOptions>? configureUi = null)
    {
        services.AddSignalShingleCache<string, string>(configureCache);
        services.AddSignalR();
        var options = new SignalShingleUiOptions();
        configureUi?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ISignalShingleNotifier, SignalShingleNotifier>();
        return services;
    }

    /// <summary>Maps the fragment endpoint and the dirty-beacon hub.</summary>
    public static IEndpointRouteBuilder MapSignalShingleUi(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<SignalShingleUiOptions>();
        endpoints.MapHub<SignalShingleHub>(options.HubPath);
        endpoints.MapGet($"{options.EndpointPrefix}/{{key}}", (string key, ISignalShingleCache<string, string> cache) =>
        {
            var read = cache.Read(key);
            return read.IsWarm
                ? Results.Content(read.Value!, "text/html; charset=utf-8")
                : Results.StatusCode(StatusCodes.Status202Accepted);
        });
        return endpoints;
    }
}
