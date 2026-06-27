using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.Atoms.Ephemeral;

public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Registers a per-(TItem,TResult) <see cref="EphemeralLlmCoordinator{TItem,TResult}"/>
    ///     plus the matching bootstrap so it constructs at startup. Caller is
    ///     responsible for registering the four collaborators
    ///     (<see cref="IEphemeralPicker{TItem}"/>, <see cref="IEphemeralPrompter{TItem}"/>,
    ///     <see cref="IEphemeralLlmInvoker{TResult}"/>,
    ///     <see cref="IEphemeralWriteback{TItem,TResult}"/>) separately.
    /// </summary>
    public static IServiceCollection AddEphemeralLlmCoordinator<TItem, TResult>(
        this IServiceCollection services,
        Action<EphemeralLlmCoordinatorOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<EphemeralLlmCoordinator<TItem, TResult>>();
        services.AddSingleton<IHostedService, EphemeralLlmCoordinatorBootstrap<TItem, TResult>>();
        return services;
    }
}
