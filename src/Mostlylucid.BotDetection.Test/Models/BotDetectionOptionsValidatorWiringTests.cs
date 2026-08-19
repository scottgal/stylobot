using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Models;

/// <summary>
///     Regression guard for the validator wiring in <c>AddBotDetectionModule</c>:
///     <c>BotDetectionOptionsValidator</c> is registered as
///     <c>IValidateOptions&lt;BotDetectionOptions&gt;</c>, so its fail-closed rejections
///     (API-key full-detector bypass configured while <c>AllowFullDetectorBypassApiKeys</c>
///     is false) actually enforce at boot — previously the validator only ran in tests and
///     a host could boot with <c>DisabledDetectors=["*"]</c>.
/// </summary>
public sealed class BotDetectionOptionsValidatorWiringTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?> overrides)
    {
        var values = new Dictionary<string, string?>
        {
            // AddBotDetection fails fast on a null DatabasePath; empty = in-memory opt-in.
            ["BotDetection:DatabasePath"] = string.Empty
        };
        foreach (var (k, v) in overrides)
            values[k] = v;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ModuleRegistration_RejectsFullDetectorBypassKey_AtOptionsResolve()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["BotDetection:ApiKeys:SB-BYPASS:Name"] = "Bypass",
            ["BotDetection:ApiKeys:SB-BYPASS:DisabledDetectors:0"] = "*"
        });

        // ValidateOnStart eager-validates at host start; resolving the options is the
        // in-process equivalent — every registered IValidateOptions<T> runs here too.
        var ex = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value);

        Assert.Contains("AllowFullDetectorBypassApiKeys", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModuleRegistration_PlainKey_ResolvesWithoutValidationError()
    {
        // Positive control: the instrumentation above fires, and the wiring does not
        // reject keys that carry no bypass overlay.
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["BotDetection:ApiKeys:SB-PLAIN:Name"] = "Plain",
            ["BotDetection:ApiKeys:SB-PLAIN:Enabled"] = "true"
        });

        var options = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.True(options.ApiKeys.ContainsKey("SB-PLAIN"));
    }
}
