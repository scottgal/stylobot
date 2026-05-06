using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class DegradationAtomTests : IDisposable
{
    private readonly DegradationAtom _atom;

    public DegradationAtomTests()
    {
        _atom = new DegradationAtom(windowSeconds: 60, emaAlpha: 0.5);
    }

    public void Dispose() => _atom.Dispose();

    [Fact]
    public void GetSignalValue_NoRequests_ReturnsZero()
    {
        Assert.Equal(0.0, _atom.GetSignalValue("response.error_rate_5xx"), precision: 6);
    }

    [Fact]
    public void RecordResponse_500_IncrementsErrorRate()
    {
        _atom.RecordResponse(500, 50, "/api/test");
        _atom.RecordResponse(200, 50, "/api/test");

        var rate = _atom.GetSignalValue("response.error_rate_5xx");
        Assert.True(rate > 0, $"Expected error rate > 0, got {rate}");
    }

    [Fact]
    public void RecordResponse_429_IncrementsWith429Rate()
    {
        _atom.RecordResponse(429, 50, "/api/test");

        var rate = _atom.GetSignalValue("response.rate_429");
        Assert.True(rate > 0, $"Expected 429 rate > 0, got {rate}");
    }

    [Fact]
    public void RecordResponse_UpdatesEndpointScopedSignal()
    {
        _atom.RecordResponse(500, 50, "/api/checkout");

        var globalRate = _atom.GetSignalValue("response.error_rate_5xx");
        var scopedRate = _atom.GetSignalValue("response.error_rate_5xx:/api/checkout");

        Assert.True(globalRate > 0);
        Assert.True(scopedRate > 0);
    }

    [Fact]
    public void RecordResponse_200_DoesNotIncrementErrorRates()
    {
        _atom.RecordResponse(200, 50, "/api/test");
        _atom.RecordResponse(200, 50, "/api/test");

        Assert.Equal(0.0, _atom.GetSignalValue("response.error_rate_5xx"), precision: 6);
        Assert.Equal(0.0, _atom.GetSignalValue("response.rate_429"), precision: 6);
    }

    [Fact]
    public void RecordResponse_LatencyTracked()
    {
        _atom.RecordResponse(200, 1500, "/api/test");

        var latency = _atom.GetSignalValue("response.latency_p95");
        Assert.True(latency > 0, $"Expected latency > 0, got {latency}");
    }

    [Fact]
    public void GetAvailableSignalKeys_IncludesBuiltInKeys()
    {
        var keys = _atom.GetAvailableSignalKeys();
        Assert.Contains("response.error_rate_5xx", keys);
        Assert.Contains("response.rate_429", keys);
        Assert.Contains("response.latency_p95", keys);
    }
}
