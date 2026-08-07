using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mostlylucid.SignalShingle;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers one local cache per key/value projection type.</summary>
    public static IServiceCollection AddSignalShingleCache<TKey, TValue>(this IServiceCollection services,
        Action<SignalShingleCacheOptions>? configure = null) where TKey : notnull
    {
        var options = new SignalShingleCacheOptions(); configure?.Invoke(options);
        services.TryAddSingleton<ISignalShingleCache<TKey, TValue>>(sp =>
            new SignalShingleCache<TKey, TValue>(options, sp.GetService<TimeProvider>()));
        return services;
    }
}
