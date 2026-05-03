using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Tests that IpContributor correctly delegates IP resolution to IProxyEnvironment,
///     writes the proxy.topology signal, and retains legacy fallback behaviour when
///     IProxyEnvironment is not registered.
/// </summary>
public class IpContributorProxyTests
{
    private readonly Mock<IDetectorConfigProvider> _configProviderMock;

    public IpContributorProxyTests()
    {
        _configProviderMock = new Mock<IDetectorConfigProvider>();
        _configProviderMock.Setup(c => c.GetDefaults(It.IsAny<string>())).Returns(new DetectorDefaults());
        _configProviderMock.Setup(c => c.GetManifest(It.IsAny<string>())).Returns((DetectorManifest?)null);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        _configProviderMock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
    }

    private IpContributor CreateContributor(IProxyEnvironment? proxyEnv = null)
    {
        return new IpContributor(
            NullLogger<IpContributor>.Instance,
            _configProviderMock.Object,
            proxyEnvironment: proxyEnv);
    }

    private static BlackboardState CreateState(
        Dictionary<string, string>? headers = null,
        string? remoteIp = null)
    {
        var ctx = new DefaultHttpContext();
        if (headers != null)
            foreach (var (k, v) in headers)
                ctx.Request.Headers[k] = v;
        if (remoteIp != null && IPAddress.TryParse(remoteIp, out var ip))
            ctx.Connection.RemoteIpAddress = ip;
        var dict = new ConcurrentDictionary<string, object>();
        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = dict,
            SignalWriter = dict,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };
    }

    private static ProxyEnvironmentDetector CreateRealDetector(string mode)
    {
        var options = new BotDetectionOptions { ProxyEnvironment = new ProxyEnvironmentOptions { Mode = mode } };
        return new ProxyEnvironmentDetector(NullLogger<ProxyEnvironmentDetector>.Instance, Options.Create(options));
    }

    // ==========================================
    // Topology signal written to blackboard
    // ==========================================

    [Fact]
    public async Task ContributeAsync_WithCloudflareTopology_WritesProxyTopologySignal()
    {
        var proxyEnv = CreateRealDetector("Cloudflare");
        var contributor = CreateContributor(proxyEnv);
        var state = CreateState(new Dictionary<string, string> { ["CF-Connecting-IP"] = "1.2.3.4" });

        await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ProxyTopology),
            "proxy.topology signal should be written");
        Assert.Equal("Cloudflare", state.Signals[SignalKeys.ProxyTopology]);
    }

    [Fact]
    public async Task ContributeAsync_WithDirectTopology_WritesDirectProxyTopologySignal()
    {
        var proxyEnv = CreateRealDetector("Direct");
        var contributor = CreateContributor(proxyEnv);
        var state = CreateState(remoteIp: "203.0.113.5");

        await contributor.ContributeAsync(state);

        Assert.Equal("Direct", state.Signals[SignalKeys.ProxyTopology]);
    }

    // ==========================================
    // IP resolution via IProxyEnvironment
    // ==========================================

    [Fact]
    public async Task ContributeAsync_CloudflareTopology_ResolvesIpFromCfConnectingIp()
    {
        var proxyEnv = CreateRealDetector("Cloudflare");
        var contributor = CreateContributor(proxyEnv);
        var state = CreateState(new Dictionary<string, string>
        {
            ["CF-Connecting-IP"] = "198.51.100.42",
            ["X-Forwarded-For"] = "10.0.0.1"   // proxy IP, should be ignored
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("198.51.100.42", state.Signals[SignalKeys.ClientIp]);
    }

    [Fact]
    public async Task ContributeAsync_CloudFrontTopology_ResolvesIpFromViewerAddress()
    {
        var proxyEnv = CreateRealDetector("CloudFront");
        var contributor = CreateContributor(proxyEnv);
        var state = CreateState(new Dictionary<string, string>
        {
            ["CloudFront-Viewer-Address"] = "203.0.113.7:12345"
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("203.0.113.7", state.Signals[SignalKeys.ClientIp]);
    }

    [Fact]
    public async Task ContributeAsync_NginxTopology_ResolvesIpFromXRealIp()
    {
        var proxyEnv = CreateRealDetector("Nginx");
        var contributor = CreateContributor(proxyEnv);
        var state = CreateState(new Dictionary<string, string>
        {
            ["X-Real-IP"] = "203.0.113.99",
            ["X-Forwarded-For"] = "10.0.0.1"   // should be ignored
        });

        await contributor.ContributeAsync(state);

        Assert.Equal("203.0.113.99", state.Signals[SignalKeys.ClientIp]);
    }

    // ==========================================
    // Legacy fallback (no IProxyEnvironment registered)
    // ==========================================

    [Fact]
    public async Task ContributeAsync_NoProxyEnvironment_PublicRemoteIp_UsesConnectionIp()
    {
        var contributor = CreateContributor(proxyEnv: null);
        var state = CreateState(remoteIp: "203.0.113.10");

        await contributor.ContributeAsync(state);

        Assert.Equal("203.0.113.10", state.Signals[SignalKeys.ClientIp]);
    }

    [Fact]
    public async Task ContributeAsync_NoProxyEnvironment_PrivateRemoteIp_FallsBackToXff()
    {
        var contributor = CreateContributor(proxyEnv: null);
        var state = CreateState(
            headers: new Dictionary<string, string> { ["X-Forwarded-For"] = "203.0.113.55, 10.0.0.1" },
            remoteIp: "172.17.0.1");  // Docker bridge - private

        await contributor.ContributeAsync(state);

        Assert.Equal("203.0.113.55", state.Signals[SignalKeys.ClientIp]);
    }

    [Fact]
    public async Task ContributeAsync_NoProxyEnvironment_WritesUnknownTopologySignal()
    {
        var contributor = CreateContributor(proxyEnv: null);
        var state = CreateState(remoteIp: "203.0.113.1");

        await contributor.ContributeAsync(state);

        Assert.True(state.Signals.ContainsKey(SignalKeys.ProxyTopology));
        Assert.Equal("Unknown", state.Signals[SignalKeys.ProxyTopology]);
    }
}
