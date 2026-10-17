using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Storage;

public sealed class StoreInitializerRegistrationTests
{
    private static ServiceCollection Register()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBotDetection();
        return services;
    }

    private static Type[] MissingInitializers(IServiceCollection services)
    {
        // Discover registered implementations, including registrations against an
        // interface. Unregistered legacy stores are deliberately outside this contract.
        var stores = services.SelectMany(descriptor => new[]
                { descriptor.ServiceType, descriptor.ImplementationType })
            .OfType<Type>()
            .Where(type => type.IsClass && !type.IsAbstract &&
                           typeof(IStoreInitializer).IsAssignableFrom(type))
            .Distinct();

        return stores.Where(store => !services.Any(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(StoreInitService<>).MakeGenericType(store)))
            .ToArray();
    }

    [Fact]
    public void Every_registered_store_initializer_has_a_hosted_startup_service()
    {
        var missing = MissingInitializers(Register());
        Assert.True(missing.Length == 0,
            "Missing hosted initialization for: " + string.Join(", ", missing.Select(type => type.Name)));
    }

    public static IEnumerable<object[]> RegisteredInitializerTypes() => Register()
        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService) &&
                             descriptor.ImplementationType is { IsGenericType: true } type &&
                             type.GetGenericTypeDefinition() == typeof(StoreInitService<>))
        .Select(descriptor => new object[] { descriptor.ImplementationType!.GetGenericArguments()[0] });

    [Theory]
    [MemberData(nameof(RegisteredInitializerTypes))]
    public void Coverage_guard_detects_each_removed_startup_registration(Type storeType)
    {
        var services = Register();
        var initializer = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(StoreInitService<>).MakeGenericType(storeType));
        services.Remove(initializer);

        Assert.Equal(new[] { storeType }, MissingInitializers(services));
    }
}
