using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Pins the contract that <see cref="BlackboardState.ClientIp"/> returns the
///     real client IP when an upstream proxy (Cloudflare / CloudFront / Fastly /
///     Nginx / generic XFF) is in front, and falls back to the raw socket address
///     when no <see cref="IProxyEnvironment"/> is registered. Closes the gap
///     where every IP-keyed contributor (VerifiedBotInline / VerifiedBot /
///     ReputationBias / FastPathReputation / BehavioralWaveform) was reading
///     Cloudflare's edge IP through a CDN-fronted gateway, causing real Googlebot
///     and Bingbot crawls to fail IP verification and land as Spoofed-* /
///     BotType=Scraper / VeryHigh risk.
/// </summary>
public sealed class BlackboardStateClientIpTests
{
    [Fact]
    public void ClientIp_BehindCloudflare_ReturnsCfConnectingIp_NotEdgeSocketAddress()
    {
        // Cloudflare sets CF-Ray + CF-Connecting-IP. The gateway socket sees a
        // Cloudflare edge address (104.16/12 etc.); BlackboardState.ClientIp must
        // resolve the real client IP from CF-Connecting-IP, otherwise every
        // visitor through the CDN collapses to one edge IP.
        const string realClientIp = "66.249.66.1"; // Googlebot range
        const string cloudflareEdgeIp = "104.16.132.229";

        var ctx = MakeHttpContext(socketIp: cloudflareEdgeIp, headers: new Dictionary<string, string>
        {
            ["CF-Ray"] = "abcd1234-LHR",
            ["CF-Connecting-IP"] = realClientIp,
        });
        var state = MakeState(ctx);

        Assert.Equal(realClientIp, state.ClientIp);
    }

    [Fact]
    public void ClientIp_NginxXRealIp_ReturnsXRealIpNotSocket()
    {
        const string realClientIp = "203.0.113.45";
        var ctx = MakeHttpContext(socketIp: "10.0.0.1", headers: new Dictionary<string, string>
        {
            ["X-Real-IP"] = realClientIp,
            ["X-Forwarded-For"] = realClientIp,
        });
        var state = MakeState(ctx);

        Assert.Equal(realClientIp, state.ClientIp);
    }

    [Fact]
    public void ClientIp_NoProxyHeaders_FallsBackToSocketAddress()
    {
        const string socketIp = "198.51.100.7";
        var ctx = MakeHttpContext(socketIp: socketIp, headers: new Dictionary<string, string>());
        var state = MakeState(ctx);

        Assert.Equal(socketIp, state.ClientIp);
    }

    [Fact]
    public void ClientIp_NoProxyEnvironmentService_FallsBackToSocketAddress()
    {
        // Unit-test / bare orchestration scenario: no IProxyEnvironment in DI.
        // The property must still return SOMETHING usable (the socket address)
        // rather than nulling out and breaking every IP-keyed detector.
        const string socketIp = "198.51.100.99";
        var ctx = MakeHttpContext(socketIp: socketIp, headers: new Dictionary<string, string>());
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();
        var state = MakeState(ctx);

        Assert.Equal(socketIp, state.ClientIp);
    }

    private static DefaultHttpContext MakeHttpContext(string socketIp, Dictionary<string, string> headers)
    {
        var ctx = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(socketIp) },
        };
        foreach (var (k, v) in headers) ctx.Request.Headers[k] = v;

        // Real IProxyEnvironment registration so the property resolves through
        // the actual auto-detect path; tests using a stripped service provider
        // exercise the fallback branch instead. AddLogging is required because
        // ProxyEnvironmentDetector takes an ILogger<T>.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProxyEnvironment, ProxyEnvironmentDetector>();
        ctx.RequestServices = services.BuildServiceProvider();
        return ctx;
    }

    private static BlackboardState MakeState(HttpContext ctx) => new()
    {
        HttpContext = ctx,
        Signals = new ConcurrentDictionary<string, object>(),
        SignalWriter = new ConcurrentDictionary<string, object>(),
        CompletedDetectors = new HashSet<string>(),
        FailedDetectors = new HashSet<string>(),
        Contributions = Array.Empty<DetectionContribution>(),
        RequestId = "test",
    };
}