using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;

namespace Mostlylucid.BotDetection.Test.Orchestration.Atoms;

/// <summary>
///     Wiring test for Task 6 (webhook-recognition-policy): <see cref="WebhookSensor"/>
///     was implemented in an earlier task but never registered with the atom
///     orchestrator, so it never ran in a real host despite passing its own unit
///     tests. This pins the DI registration itself, mirroring how
///     <c>RegistryClientSensor</c>'s registration is exercised elsewhere.
/// </summary>
public sealed class WebhookSensorRegistrationTests
{
    [Fact]
    public void WebhookSensor_is_registered_as_detector_atom()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        // AddBotDetection fails fast on a null DatabasePath (see
        // ServiceCollectionExtensionsTests); this ephemeral registration-only test opts
        // into in-memory explicitly, same as AddBotDetectionInMemory does.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = string.Empty,
            })
            .Build());

        services.AddBotDetection(); // the FOSS entrypoint
        var provider = services.BuildServiceProvider();
        provider.GetServices<IDetectorAtom>().Should().Contain(a => a.GetType() == typeof(WebhookSensor));
    }
}
