using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for DeterministicBotNameSynthesizer - generates meaningful names from detection signals.
/// </summary>
public class DeterministicBotNameTests
{
    private readonly DeterministicBotNameSynthesizer _synthesizer = new();

    [Fact]
    public async Task IsReady_AlwaysTrue()
    {
        Assert.True(_synthesizer.IsReady);
    }

    [Theory]
    [InlineData("curl", "curl")]
    [InlineData("Scrapy", "Scrapy")]
    [InlineData("python-requests", "python-requests")]
    [InlineData("Googlebot", "Googlebot")]
    public async Task KnownBotName_UsedDirectly(string botName, string expected)
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = botName,
            ["ua.bot_type"] = "Tool"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.Equal(expected, name);
    }

    [Fact]
    public async Task UnknownBot_GeneratesDescriptiveName()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = null,
            ["ua.bot_type"] = "Scraper",
            ["ua.family"] = "python-requests",
            ["intent.category"] = "scraping",
            ["waveform.page_rate"] = 25.0,
            ["waveform.asset_ratio"] = 0.0
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotNull(name);
        Assert.NotEqual("Unknown", name);
        Assert.NotEqual("unknown", name);
        // Should contain a meaningful descriptor
        Assert.True(name!.Length > 3, $"Name should be descriptive, got '{name}'");
    }

    [Fact]
    public async Task HighVelocity_GetsRotatingPrefix()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = null,
            ["ua.bot_type"] = "Scraper",
            ["session.velocity_magnitude"] = 0.8,
            ["intent.category"] = "scraping"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.Contains("Rotating", name);
    }

    [Fact]
    public async Task NoAssets_GetsHeadlessPrefix()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = null,
            ["ua.bot_type"] = "Tool",
            ["waveform.asset_ratio"] = 0.0,
            ["waveform.page_rate"] = 10.0,
            ["ua.family"] = "python"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.Contains("Headless", name);
    }

    [Fact]
    public async Task ScanningIntent_GetsScannerNoun()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = null,
            ["intent.category"] = "scanning"
        };

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.Contains("Scanner", name);
    }

    [Fact]
    public async Task SynthesizeDetailed_ReturnsNameAndDescription()
    {
        var signals = new Dictionary<string, object?>
        {
            ["ua.bot_name"] = null,
            ["ua.family"] = "curl",
            ["ua.bot_type"] = "Tool",
            ["intent.category"] = "scanning",
            ["waveform.page_rate"] = 15.0
        };

        var (name, desc) = await _synthesizer.SynthesizeDetailedAsync(signals);

        Assert.NotNull(name);
        Assert.NotNull(desc);
        Assert.Contains("curl", desc, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptySignals_ReturnsGenericName()
    {
        var signals = new Dictionary<string, object?>();

        var name = await _synthesizer.SynthesizeBotNameAsync(signals);

        Assert.NotNull(name);
        // Should still produce something, not null or empty
        Assert.True(name!.Length > 0);
    }
}
