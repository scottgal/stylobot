using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Extensions;

/// <summary>
///     Tests for BotDetection ServiceCollectionExtensions
/// </summary>
public class ServiceCollectionExtensionsTests
{
    /// <summary>
    ///     Creates an empty IConfiguration for tests.
    ///     Required because AddBotDetection() uses BindConfiguration which needs IConfiguration.
    /// </summary>
    private static IConfiguration CreateEmptyConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // AddBotDetection now fails fast when DatabasePath is null (a null
                // path used to fall back SILENTLY to an unbounded in-memory SQLite DB).
                // These ephemeral option-binding tests never persist, so they opt into
                // in-memory explicitly the same way AddBotDetectionInMemory does:
                // DatabasePath = empty. Empty passes validation; only null is rejected.
                ["BotDetection:DatabasePath"] = string.Empty,
            })
            .Build();
    }

    /// <summary>
    ///     Adds standard test dependencies (logging, cache, configuration)
    /// </summary>
    private static void AddTestDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(CreateEmptyConfiguration());
    }

    #region DatabasePath fail-loud contract

    /// <summary>
    ///     Regression guard for the silent-in-memory drift, restated at the level of the
    ///     INTENT rather than the mechanism.
    ///
    ///     <para>
    ///         The original outage: an unset DatabasePath fell back silently to
    ///         <c>Data Source=file::memory:</c>, which grows unbounded and OOMs the process
    ///         (found via soak+load). The first fix made "unset" throw. That was correct about
    ///         the danger and wrong about the remedy: it also broke
    ///         <c>AddBotDetection()</c> and <c>AddSimpleBotDetection()</c> with no
    ///         configuration — the two minimal entry points CLAUDE.md documents — so the
    ///         published getting-started path crashed at startup. Confirmed standalone
    ///         2026-08-08 on a bare WebApplication.
    ///     </para>
    ///
    ///     <para>
    ///         <b>What must remain true is not "unset throws" — it is "we never silently run
    ///         on an unbounded in-memory database".</b> That is now guaranteed BY
    ///         CONSTRUCTION: unset resolves to an on-disk file under
    ///         <c>AppContext.BaseDirectory</c>. This test asserts the guarantee directly, so
    ///         it still fails if anyone reintroduces an in-memory fallback — including via a
    ///         "convenient" default — which a throw-on-unset assertion could not distinguish
    ///         from a legitimate default.
    ///     </para>
    /// </summary>
    [Fact]
    public void AddBotDetection_UnconfiguredDatabasePath_DefaultsToDisk_NeverSilentInMemory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        // Deliberately NO DatabasePath key -- the documented minimal setup.
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();

        // The minimal path must START, not crash. This is the shipped-defect half.
        var options = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        // ...and it must land on a real file, never the unbounded in-memory DB.
        Assert.False(string.IsNullOrEmpty(options.DatabasePath),
            "unset DatabasePath must resolve to a concrete on-disk default; empty is the "
            + "explicit AddBotDetectionInMemory() opt-in and must not happen implicitly");
        Assert.DoesNotContain(":memory:", options.DatabasePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("botdetection.db", options.DatabasePath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathRooted(options.DatabasePath),
            $"default should be rooted so it does not depend on the working directory; got '{options.DatabasePath}'");
    }

    /// <summary>
    ///     The fail-loud half of the contract survives: an EXPLICIT null is still a
    ///     configuration error and still throws with actionable guidance. Only "the operator
    ///     said nothing" is now serviced by a default; "the operator explicitly nulled it"
    ///     remains loud.
    /// </summary>
    [Fact]
    public void AddBotDetection_ExplicitNullDatabasePath_StillThrowsWithGuidance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddBotDetection(o => o.DatabasePath = null);

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value);
        Assert.Contains("DatabasePath", ex.Message);
        Assert.Contains("AddBotDetectionInMemory", ex.Message);
    }

    /// <summary>
    ///     The explicit ephemeral opt-in (empty DatabasePath, as AddBotDetectionInMemory
    ///     sets) is the ONLY allowed in-memory path and must NOT throw.
    /// </summary>
    [Fact]
    public void AddBotDetection_EmptyDatabasePath_IsAllowed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = string.Empty,
            })
            .Build());
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.Equal(string.Empty, options.DatabasePath);
    }

    #endregion

    #region Multiple Registration Tests

    [Fact]
    public void AddBotDetection_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act - Should not throw
        services.AddBotDetection();
        var exception = Record.Exception(() => services.AddBotDetection());

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region AddBotDetection Tests

    [Fact]
    public void AddBotDetection_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddBotDetection_WithOptions_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection(options =>
        {
            options.BotThreshold = 0.8;
            options.EnableLlmDetection = true;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.NotNull(options);
        Assert.Equal(0.8, options.Value.BotThreshold);
        Assert.True(options.Value.EnableLlmDetection);
    }

    [Fact]
    public void AddBotDetection_RegistersBotDetectionService()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var service = provider.GetService<IBotDetectionService>();
        Assert.NotNull(service);
    }

    #endregion

    #region Options Configuration Tests

    [Fact]
    public void AddBotDetection_NullOptions_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.NotNull(options);
        Assert.Equal(0.7, options.Value.BotThreshold); // Default value
    }

    [Fact]
    public void AddBotDetection_CustomOptions_Preserved()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);
        var customPatterns = new List<string> { "CustomBot1", "CustomBot2" };

        // Act
        services.AddBotDetection(options =>
        {
            options.WhitelistedBotPatterns = customPatterns;
            options.MaxRequestsPerMinute = 120;
            options.CacheDurationSeconds = 600;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.Equal(customPatterns, options!.Value.WhitelistedBotPatterns);
        Assert.Equal(120, options.Value.MaxRequestsPerMinute);
        Assert.Equal(600, options.Value.CacheDurationSeconds);
    }

    #endregion

    #region Return Value Tests

    [Fact]
    public void AddBotDetection_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        var result = services.AddBotDetection();

        // Assert
        Assert.Same(services, result);
    }

    [Fact]
    public void AddBotDetection_WithOptions_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        var result = services.AddBotDetection(options => { });

        // Assert
        Assert.Same(services, result);
    }

    #endregion

    #region Upstream health / degradation tracking (passive per-request wiring)

    /// <summary>
    ///     Regression guard: <see cref="RateLimit.DegradationAtom"/> was defined and
    ///     unit-tested but never registered in DI, so the aggregate upstream 5xx/4xx
    ///     rate it tracks was never fed from real traffic and the dashboard's
    ///     "Upstream healthy" card always showed the empty-data default regardless of
    ///     actual outages.
    /// </summary>
    [Fact]
    public void AddBotDetection_RegistersDegradationAtom()
    {
        var services = new ServiceCollection();
        AddTestDependencies(services);

        services.AddBotDetection();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<DegradationAtom>());
    }

    /// <summary>
    ///     Companion registration: without this, <see cref="RateLimit.UpstreamHealthGate"/>
    ///     consumers (ClaimedIdentityAtom, HeuristicDetector, GatewayWarmupGate) always
    ///     resolve null and the outage-protection gate never engages.
    /// </summary>
    [Fact]
    public void AddBotDetection_RegistersUpstreamHealthGate()
    {
        var services = new ServiceCollection();
        AddTestDependencies(services);

        services.AddBotDetection();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<UpstreamHealthGate>());
    }

    #endregion
}
