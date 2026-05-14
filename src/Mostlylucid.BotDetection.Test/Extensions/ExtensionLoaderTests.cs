using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.Test.Extensions;

public class ExtensionLoaderTests
{
    [Fact]
    public void NoExtensionsConfigured_NoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddStyloBotExtensions(config);

        Assert.Empty(services);
    }

    [Fact]
    public void EmptyAssemblyPaths_NoOp()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Section exists but no paths.
                ["BotDetection:Extensions:ContinueOnLoadFailure"] = "true"
            }).Build();

        services.AddStyloBotExtensions(config);

        Assert.Empty(services);
    }

    [Fact]
    public void InvalidPath_ContinuesByDefault()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Extensions:AssemblyPaths:0"] = "does-not-exist.dll"
            }).Build();

        var ex = Record.Exception(() => services.AddStyloBotExtensions(config));
        Assert.Null(ex);
    }

    [Fact]
    public void InvalidPath_Throws_WhenContinueOnLoadFailureFalse()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Extensions:AssemblyPaths:0"] = "does-not-exist.dll",
                ["BotDetection:Extensions:ContinueOnLoadFailure"] = "false"
            }).Build();

        Assert.ThrowsAny<Exception>(() => services.AddStyloBotExtensions(config));
    }

    [Fact]
    public void ValidExtensionAssembly_CallsConfigure()
    {
        // Use this test assembly itself - it contains TestBotDetectionExtension below.
        var thisAssemblyPath = typeof(ExtensionLoaderTests).Assembly.Location;
        Assert.False(string.IsNullOrEmpty(thisAssemblyPath));

        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Extensions:AssemblyPaths:0"] = thisAssemblyPath
            }).Build();

        services.AddStyloBotExtensions(config);

        // TestBotDetectionExtension registers a marker singleton.
        var sp = services.BuildServiceProvider();
        var marker = sp.GetService<TestExtensionMarker>();
        Assert.NotNull(marker);
        Assert.Equal("loaded-by-extension", marker!.Tag);
    }
}

// Public so the loader's GetExportedTypes() can find it.
public sealed class TestBotDetectionExtension : IBotDetectionExtension
{
    public string Name => "TestExtension";
    public Version Version => new(1, 0, 0);

    public void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new TestExtensionMarker("loaded-by-extension"));
    }
}

public sealed record TestExtensionMarker(string Tag);
