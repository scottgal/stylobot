using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Unit tests for ProxyEnvironmentDetector.
///     Covers topology auto-detection, per-topology IP extraction, scheme inference,
///     config override, and thread-safe one-time caching.
/// </summary>
public class ProxyEnvironmentDetectorTests
{
    // ==========================================
    // Helpers
    // ==========================================

    private static ProxyEnvironmentDetector CreateDetector(string mode = "Auto")
    {
        var options = new BotDetectionOptions
        {
            ProxyEnvironment = new ProxyEnvironmentOptions { Mode = mode }
        };
        return new ProxyEnvironmentDetector(
            NullLogger<ProxyEnvironmentDetector>.Instance,
            Options.Create(options));
    }

    private static DefaultHttpContext CreateContext(
        Dictionary<string, string>? headers = null,
        string? remoteIp = null)
    {
        var ctx = new DefaultHttpContext();
        if (headers != null)
            foreach (var (k, v) in headers)
                ctx.Request.Headers[k] = v;
        if (remoteIp != null && IPAddress.TryParse(remoteIp, out var ip))
            ctx.Connection.RemoteIpAddress = ip;
        return ctx;
    }

    // ==========================================
    // Topology detection - auto mode
    // ==========================================

    [Fact]
    public void DetectedTopology_NoHeaders_ReturnsDirect()
    {
        var detector = CreateDetector();
        var ctx = CreateContext();

        _ = detector.GetRealClientIp(ctx); // triggers detection

        Assert.Equal(ProxyTopology.Direct, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_CfConnectingIpHeader_ReturnsCloudflare()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["CF-Connecting-IP"] = "1.2.3.4" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_CfRayHeader_ReturnsCloudflare()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["CF-Ray"] = "abc123-LHR" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_CloudFrontViewerAddressHeader_ReturnsCloudFront()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["CloudFront-Viewer-Address"] = "1.2.3.4:54321" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.CloudFront, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_XAmzCfIdHeader_ReturnsCloudFront()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["X-Amz-Cf-Id"] = "somevalue" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.CloudFront, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_FastlyClientIpHeader_ReturnsFastly()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["Fastly-Client-IP"] = "5.6.7.8" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Fastly, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_XRealIpHeader_ReturnsNginx()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["X-Real-IP"] = "9.10.11.12" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Nginx, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_XForwardedForOnly_ReturnsGeneric()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["X-Forwarded-For"] = "20.30.40.50" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Generic, detector.DetectedTopology);
    }

    // ==========================================
    // Config override - mode takes precedence
    // ==========================================

    [Fact]
    public void DetectedTopology_ModeCloudflare_IgnoresHeaders()
    {
        var detector = CreateDetector(mode: "Cloudflare");

        // Even with no CF headers, should be Cloudflare from config
        var ctx = CreateContext();
        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_ModeDirect_IgnoresXForwardedForHeader()
    {
        var detector = CreateDetector(mode: "Direct");
        var ctx = CreateContext(new Dictionary<string, string> { ["X-Forwarded-For"] = "1.2.3.4" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Direct, detector.DetectedTopology);
    }

    [Theory]
    [InlineData("cloudflare", ProxyTopology.Cloudflare)]
    [InlineData("CLOUDFLARE", ProxyTopology.Cloudflare)]
    [InlineData("CloudFront", ProxyTopology.CloudFront)]
    [InlineData("fastly", ProxyTopology.Fastly)]
    [InlineData("nginx", ProxyTopology.Nginx)]
    [InlineData("generic", ProxyTopology.Generic)]
    [InlineData("direct", ProxyTopology.Direct)]
    public void DetectedTopology_ModeIsCaseInsensitive(string mode, ProxyTopology expected)
    {
        var detector = CreateDetector(mode: mode);
        _ = detector.GetRealClientIp(CreateContext());
        Assert.Equal(expected, detector.DetectedTopology);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("auto")]
    [InlineData("")]
    public void DetectedTopology_AutoMode_PerformsAutoDetection(string mode)
    {
        var detector = CreateDetector(mode: mode);
        var ctx = CreateContext(new Dictionary<string, string> { ["CF-Connecting-IP"] = "1.2.3.4" });

        _ = detector.GetRealClientIp(ctx);

        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }

    // ==========================================
    // IP extraction - per topology
    // ==========================================

    [Fact]
    public void GetRealClientIp_Cloudflare_ReadsCfConnectingIp()
    {
        var detector = CreateDetector(mode: "Cloudflare");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["CF-Connecting-IP"] = "1.2.3.4",
            ["X-Forwarded-For"] = "10.0.0.1"  // should be ignored
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("1.2.3.4", ip);
    }

    [Fact]
    public void GetRealClientIp_CloudFront_ParsesViewerAddressStrippingPort()
    {
        var detector = CreateDetector(mode: "CloudFront");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["CloudFront-Viewer-Address"] = "203.0.113.42:54321"
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("203.0.113.42", ip);
    }

    [Fact]
    public void GetRealClientIp_Fastly_ReadsFastlyClientIp()
    {
        var detector = CreateDetector(mode: "Fastly");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["Fastly-Client-IP"] = "5.6.7.8",
            ["X-Forwarded-For"] = "10.0.0.1"  // should be ignored
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("5.6.7.8", ip);
    }

    [Fact]
    public void GetRealClientIp_Nginx_ReadsXRealIp()
    {
        var detector = CreateDetector(mode: "Nginx");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Real-IP"] = "9.10.11.12",
            ["X-Forwarded-For"] = "10.0.0.1"  // should be ignored
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("9.10.11.12", ip);
    }

    [Fact]
    public void GetRealClientIp_Generic_ReadsLeftmostXForwardedFor()
    {
        var detector = CreateDetector(mode: "Generic");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "20.30.40.50, 10.0.0.1, 172.16.0.1"
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("20.30.40.50", ip);
    }

    [Fact]
    public void GetRealClientIp_Direct_ReadsRemoteIpAddress()
    {
        var detector = CreateDetector(mode: "Direct");
        var ctx = CreateContext(remoteIp: "198.51.100.1");

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("198.51.100.1", ip);
    }

    [Fact]
    public void GetRealClientIp_Direct_NoRemoteIp_ReturnsEmpty()
    {
        var detector = CreateDetector(mode: "Direct");
        var ctx = CreateContext(); // no RemoteIpAddress

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal(string.Empty, ip);
    }

    // ==========================================
    // Fallback chain
    // ==========================================

    [Fact]
    public void GetRealClientIp_CloudflareMissingCfHeader_FallsBackToXff()
    {
        var detector = CreateDetector(mode: "Cloudflare");
        // CF-Connecting-IP absent, but XFF is present
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "1.2.3.4, 10.0.0.1"
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("1.2.3.4", ip);
    }

    [Fact]
    public void GetRealClientIp_CloudFrontMissingViewerAddress_FallsBackToXff()
    {
        var detector = CreateDetector(mode: "CloudFront");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = "203.0.113.99, 10.0.0.1"
        });

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal("203.0.113.99", ip);
    }

    [Fact]
    public void GetRealClientIp_NoHeadersAndNoRemoteIp_ReturnsEmpty()
    {
        var detector = CreateDetector(mode: "Generic");
        var ctx = CreateContext(); // no headers, no RemoteIpAddress

        var ip = detector.GetRealClientIp(ctx);

        Assert.Equal(string.Empty, ip);
    }

    // ==========================================
    // Scheme inference
    // ==========================================

    [Fact]
    public void GetRealScheme_Cloudflare_AlwaysHttps()
    {
        var detector = CreateDetector(mode: "Cloudflare");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "http"  // should be overridden
        });
        ctx.Request.Scheme = "http";

        var scheme = detector.GetRealScheme(ctx);

        Assert.Equal("https", scheme);
    }

    [Fact]
    public void GetRealScheme_NonCloudflare_ReadsXForwardedProto()
    {
        var detector = CreateDetector(mode: "Nginx");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https"
        });
        ctx.Request.Scheme = "http";

        var scheme = detector.GetRealScheme(ctx);

        Assert.Equal("https", scheme);
    }

    [Fact]
    public void GetRealScheme_NoHeader_FallsBackToRequestScheme()
    {
        var detector = CreateDetector(mode: "Nginx");
        var ctx = CreateContext();
        ctx.Request.Scheme = "http";

        var scheme = detector.GetRealScheme(ctx);

        Assert.Equal("http", scheme);
    }

    [Fact]
    public void GetRealScheme_XForwardedProtoMultipleValues_TakesFirst()
    {
        var detector = CreateDetector(mode: "Generic");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "https, http"
        });

        var scheme = detector.GetRealScheme(ctx);

        Assert.Equal("https", scheme);
    }

    [Fact]
    public void GetRealScheme_XForwardedProto_NormalizedToLowercase()
    {
        var detector = CreateDetector(mode: "Generic");
        var ctx = CreateContext(new Dictionary<string, string>
        {
            ["X-Forwarded-Proto"] = "HTTPS"
        });

        var scheme = detector.GetRealScheme(ctx);

        Assert.Equal("https", scheme);
    }

    // ==========================================
    // One-time caching behaviour
    // ==========================================

    [Fact]
    public void DetectedTopology_CachedAfterFirstRequest_NotRedetectedOnSecondRequest()
    {
        var detector = CreateDetector();

        // First request - Cloudflare
        var ctx1 = CreateContext(new Dictionary<string, string> { ["CF-Connecting-IP"] = "1.1.1.1" });
        _ = detector.GetRealClientIp(ctx1);
        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);

        // Second request - no CF headers (simulates a different client hitting the same instance)
        var ctx2 = CreateContext(new Dictionary<string, string> { ["X-Forwarded-For"] = "2.2.2.2" });
        _ = detector.GetRealClientIp(ctx2);

        // Topology should NOT change - it was cached from first request
        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }

    [Fact]
    public void DetectedTopology_InitialValueBeforeFirstRequest_IsDirect()
    {
        var detector = CreateDetector();
        // No request has been seen yet
        Assert.Equal(ProxyTopology.Direct, detector.DetectedTopology);
    }

    // ==========================================
    // Thread safety
    // ==========================================

    [Fact]
    public async Task DetectedTopology_ConcurrentFirstRequests_TopologySetExactlyOnce()
    {
        var detector = CreateDetector();
        var ctx = CreateContext(new Dictionary<string, string> { ["CF-Connecting-IP"] = "1.1.1.1" });

        // Simulate many concurrent first requests
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => detector.GetRealClientIp(ctx)))
            .ToArray();

        await Task.WhenAll(tasks);

        // Topology must be Cloudflare regardless of which thread won
        Assert.Equal(ProxyTopology.Cloudflare, detector.DetectedTopology);
    }
}
