using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Centroids;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Data;

public class CentroidWriterOptionsTests
{
    [Fact]
    public void OptionsBinding_WithCustomValues_ReturnsConfiguredValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "BotDetection:CentroidWriter:ChannelCapacity", "64" },
                { "BotDetection:CentroidWriter:SamplingThreshold", "0.2" }
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<CentroidWriterOptions>(config.GetSection(CentroidWriterOptions.SectionName));
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CentroidWriterOptions>>();

        Assert.Equal(64, options.Value.ChannelCapacity);
        Assert.Equal(0.2, options.Value.SamplingThreshold);
    }

    [Fact]
    public void Constructor_WithoutConfiguration_ReturnsDefaults()
    {
        var options = new CentroidWriterOptions();

        Assert.Equal(2048, options.ChannelCapacity);
        Assert.Equal(0.05, options.SamplingThreshold);
        Assert.Equal(604800, options.DecisionHalfLifeSeconds);
    }

    [Fact]
    public void MessageSubtypes_PatternMatch_ReturnsCorrectTableNames()
    {
        var sessionRow = new SessionCentroidRow
        {
            SignatureId = "test",
            Vector = [],
            IsBot = false,
            BotProbability = 0.0
        };

        var sessionMsg = new CentroidWriteMessage.SessionCentroidWrite(sessionRow);
        var signatureMsg = new CentroidWriteMessage.SignatureCentroidWrite("sig1", [], true, 0.9);
        var intentMsg = new CentroidWriteMessage.IntentCentroidWrite("sig2", [], 0.75, "Attack");

        var sessionTable = GetTableName(sessionMsg);
        var signatureTable = GetTableName(signatureMsg);
        var intentTable = GetTableName(intentMsg);

        Assert.Equal("session_centroids", sessionTable);
        Assert.Equal("signature_centroids", signatureTable);
        Assert.Equal("intent_centroids", intentTable);
    }

    private static string GetTableName(CentroidWriteMessage message) =>
        message switch
        {
            CentroidWriteMessage.SessionCentroidWrite => "session_centroids",
            CentroidWriteMessage.SignatureCentroidWrite => "signature_centroids",
            CentroidWriteMessage.IntentCentroidWrite => "intent_centroids",
            _ => throw new InvalidOperationException("Unknown message type")
        };
}
