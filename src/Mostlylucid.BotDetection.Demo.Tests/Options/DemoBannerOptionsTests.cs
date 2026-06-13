using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Demo.Options;
using Xunit;

namespace Mostlylucid.BotDetection.Demo.Tests.Options;

public sealed class DemoBannerOptionsTests
{
    [Fact]
    public void Defaults_are_set_when_no_configuration_is_provided()
    {
        var opts = new DemoBannerOptions();

        Assert.True(opts.Enabled);
        Assert.Equal("Live demo of stylobot FOSS controls on a real ASP.NET app.", opts.Text);
        Assert.Equal("https://github.com/scottgal/stylobot", opts.SourceUrl);
        Assert.Equal("https://stylobot.net/packs/aspnet", opts.PackUrl);
    }

    [Fact]
    public void Configuration_binding_overrides_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Banner:Enabled"] = "false",
                ["Demo:Banner:Text"] = "Different copy",
                ["Demo:Banner:SourceUrl"] = "https://example.test/src",
                ["Demo:Banner:PackUrl"] = "https://example.test/packs/x"
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<DemoBannerOptions>(config.GetSection("Demo:Banner"));
        var sp = services.BuildServiceProvider();

        var opts = sp.GetRequiredService<IOptions<DemoBannerOptions>>().Value;

        Assert.False(opts.Enabled);
        Assert.Equal("Different copy", opts.Text);
        Assert.Equal("https://example.test/src", opts.SourceUrl);
        Assert.Equal("https://example.test/packs/x", opts.PackUrl);
    }
}
