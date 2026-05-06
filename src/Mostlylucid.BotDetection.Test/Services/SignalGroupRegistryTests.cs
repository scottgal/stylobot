using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignalGroupRegistryTests
{
    [Fact]
    public void Resolve_KnownGroup_ReturnsSignalKeys()
    {
        var groups = new List<SignalGroupDefinition>
        {
            new() { Name = "upstream-health", Signals = ["response.error_rate_5xx", "response.rate_429"] }
        };
        var registry = new SignalGroupRegistry(groups);

        var keys = registry.Resolve("$upstream-health");

        Assert.Equal(new[] { "response.error_rate_5xx", "response.rate_429" }, keys);
    }

    [Fact]
    public void Resolve_UnknownGroup_ReturnsEmpty()
    {
        var registry = new SignalGroupRegistry([]);
        var keys = registry.Resolve("$nonexistent");
        Assert.Empty(keys);
    }

    [Fact]
    public void Resolve_NonGroupReference_ReturnsEmpty()
    {
        var registry = new SignalGroupRegistry([]);
        var keys = registry.Resolve("response.error_rate_5xx");
        Assert.Empty(keys);
    }

    [Fact]
    public void TryGetGroup_ExistingName_ReturnsTrue()
    {
        var groups = new List<SignalGroupDefinition>
        {
            new() { Name = "test-group", Signals = ["a", "b"] }
        };
        var registry = new SignalGroupRegistry(groups);

        var found = registry.TryGetGroup("test-group", out var signals);

        Assert.True(found);
        Assert.Equal(new[] { "a", "b" }, signals);
    }

    [Fact]
    public void TryGetGroup_MissingName_ReturnsFalse()
    {
        var registry = new SignalGroupRegistry([]);
        var found = registry.TryGetGroup("missing", out _);
        Assert.False(found);
    }
}
