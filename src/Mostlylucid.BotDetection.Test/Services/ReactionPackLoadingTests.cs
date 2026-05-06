using Mostlylucid.BotDetection.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionPackLoadingTests
{
    private static ReactionPackDefinition LoadPack(string resourceFragment)
    {
        var assembly = typeof(ReactionPackDefinition).Assembly;
        using var stream = assembly.GetManifestResourceStream(
            assembly.GetManifestResourceNames().Single(n => n.Contains(resourceFragment)));
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<ReactionPackDefinition>(reader.ReadToEnd());
    }

    [Fact]
    public void ErrorSpikeProtectionPack_DeserializesCorrectly()
    {
        var pack = LoadPack("error-spike-protection");

        Assert.Equal("error-spike-protection", pack.Name);
        Assert.True(pack.IsGlobal);
        Assert.Equal(3, pack.Steps.Count);
        Assert.All(pack.Steps, s => Assert.False(string.IsNullOrEmpty(s.Policy)));
        Assert.Equal("throttle-gentle", pack.Steps[0].Policy);
        Assert.Equal("block-soft", pack.Steps[2].Policy);
    }

    [Fact]
    public void LatencyProtectionPack_DeserializesCorrectly()
    {
        var pack = LoadPack("latency-protection");

        Assert.Equal("latency-protection", pack.Name);
        Assert.True(pack.IsGlobal);
        Assert.Equal(2, pack.Steps.Count);
    }

    [Fact]
    public void CheckoutProtectionPack_HasEndpointScope()
    {
        var pack = LoadPack("checkout-protection");

        Assert.False(pack.IsGlobal);
        Assert.Equal("/api/checkout", pack.ScopedEndpoint);
        Assert.Equal(10, pack.Priority);
        Assert.Equal("challenge-pow", pack.Steps[0].Policy);
    }
}
