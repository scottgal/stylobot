using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Proxy;

namespace Mostlylucid.BotDetection.Test.Proxy;

/// <summary>
///     Locks in the tunnel-environment inspector behaviour. The inspector observes
///     the first N requests at startup, decides whether the gateway is behind a
///     tunnel AND whether TLS/JA3 enrichment is reaching it, then exposes a frozen
///     snapshot the dashboard reads to render its "tunnel-without-enrichment" banner.
///     The verdict is intentionally one-shot per process restart.
/// </summary>
public class TunnelEnvironmentInspectorTests
{
    private static (TunnelEnvironmentInspector Inspector, FakeProxyEnvironment Proxy) Build(
        int minSamples = 3,
        ProxyTopology topology = ProxyTopology.Direct,
        bool enabled = true)
    {
        var proxy = new FakeProxyEnvironment(topology);
        var opts = new BotDetectionOptions
        {
            TunnelEnvironment = new TunnelEnvironmentOptions { Enabled = enabled, MinSamples = minSamples }
        };
        var inspector = new TunnelEnvironmentInspector(
            proxy,
            NullLogger<TunnelEnvironmentInspector>.Instance,
            Options.Create(opts));
        return (inspector, proxy);
    }

    private static HttpContext Request(Action<IHeaderDictionary>? configureHeaders = null)
    {
        var ctx = new DefaultHttpContext();
        configureHeaders?.Invoke(ctx.Request.Headers);
        return ctx;
    }

    [Fact]
    public void Snapshot_is_null_until_min_samples_observed()
    {
        var (inspector, _) = Build(minSamples: 3);
        inspector.Observe(Request());
        inspector.Observe(Request());
        Assert.Null(inspector.GetSnapshot());
    }

    [Fact]
    public void Snapshot_settles_after_min_samples()
    {
        var (inspector, _) = Build(minSamples: 3);
        inspector.Observe(Request());
        inspector.Observe(Request());
        inspector.Observe(Request());
        Assert.NotNull(inspector.GetSnapshot());
    }

    [Fact]
    public void Direct_with_no_forwarded_headers_settles_as_no_proxy()
    {
        var (inspector, _) = Build(minSamples: 2, topology: ProxyTopology.Direct);
        inspector.Observe(Request());
        inspector.Observe(Request());

        var snap = inspector.GetSnapshot();
        Assert.NotNull(snap);
        Assert.False(snap!.BehindProxy);
        Assert.False(snap.IsTunnelWithoutEnrichment);
    }

    [Fact]
    public void Cloudflare_topology_without_tls_headers_flags_tunnel_without_enrichment()
    {
        var (inspector, _) = Build(minSamples: 2, topology: ProxyTopology.Cloudflare);
        inspector.Observe(Request(h => h["CF-Connecting-IP"] = "1.2.3.4"));
        inspector.Observe(Request(h => h["CF-Connecting-IP"] = "1.2.3.4"));

        var snap = inspector.GetSnapshot();
        Assert.NotNull(snap);
        Assert.True(snap!.BehindProxy);
        Assert.False(snap.HasNativeTls);
        Assert.False(snap.HasForwardedTlsHeaders);
        Assert.False(snap.HasJa3OrJa4);
        Assert.True(snap.IsTunnelWithoutEnrichment);
    }

    [Fact]
    public void Cloudflare_with_forwarded_tls_clears_enrichment_warning()
    {
        var (inspector, _) = Build(minSamples: 2, topology: ProxyTopology.Cloudflare);
        inspector.Observe(Request(h =>
        {
            h["CF-Connecting-IP"] = "1.2.3.4";
            h["X-Client-TLS-Version"] = "TLSv1.3";
        }));
        inspector.Observe(Request(h =>
        {
            h["CF-Connecting-IP"] = "1.2.3.4";
            h["X-Client-TLS-Version"] = "TLSv1.3";
        }));

        var snap = inspector.GetSnapshot();
        Assert.NotNull(snap);
        Assert.True(snap!.BehindProxy);
        Assert.True(snap.HasForwardedTlsHeaders);
        Assert.False(snap.HasJa3OrJa4);
        Assert.False(snap.IsTunnelWithoutEnrichment);
        Assert.True(snap.IsPartiallyEnriched);
    }

    [Fact]
    public void Forwarded_ja3_marks_fully_enriched()
    {
        var (inspector, _) = Build(minSamples: 2, topology: ProxyTopology.Cloudflare);
        inspector.Observe(Request(h =>
        {
            h["CF-Connecting-IP"] = "1.2.3.4";
            h["X-Client-TLS-Version"] = "TLSv1.3";
            h["X-JA3-Hash"] = "abc123";
        }));
        inspector.Observe(Request(h =>
        {
            h["CF-Connecting-IP"] = "1.2.3.4";
            h["X-Client-TLS-Version"] = "TLSv1.3";
            h["X-JA3-Hash"] = "abc123";
        }));

        var snap = inspector.GetSnapshot();
        Assert.NotNull(snap);
        Assert.True(snap!.BehindProxy);
        Assert.True(snap.HasForwardedTlsHeaders);
        Assert.True(snap.HasJa3OrJa4);
        Assert.False(snap.IsTunnelWithoutEnrichment);
        Assert.False(snap.IsPartiallyEnriched);
    }

    [Fact]
    public void XForwardedFor_without_topology_match_still_counts_as_proxy()
    {
        // Topology auto-detect cannot distinguish a plain X-Forwarded-For-emitting
        // reverse proxy from "Direct", so the inspector also checks the header set
        // independently. Otherwise an nginx-without-X-Real-IP shape would slip past.
        var (inspector, _) = Build(minSamples: 1, topology: ProxyTopology.Direct);
        inspector.Observe(Request(h => h["X-Forwarded-For"] = "1.2.3.4"));

        var snap = inspector.GetSnapshot();
        Assert.NotNull(snap);
        Assert.True(snap!.BehindProxy);
    }

    [Fact]
    public void Snapshot_is_locked_in_after_settling()
    {
        // Once settled, later observations must not move the verdict -- the dashboard
        // banner relies on a stable status across the rest of the process lifetime.
        // This regression test exists because the previous (no-tunnel-detection) state
        // silently rendered a hobbled fingerprint surface as if nothing were wrong.
        var (inspector, _) = Build(minSamples: 2, topology: ProxyTopology.Cloudflare);
        inspector.Observe(Request(h => h["CF-Connecting-IP"] = "1.2.3.4"));
        inspector.Observe(Request(h => h["CF-Connecting-IP"] = "1.2.3.4"));

        var settled = inspector.GetSnapshot();
        Assert.NotNull(settled);
        Assert.True(settled!.IsTunnelWithoutEnrichment);

        // Later requests bring full enrichment, but the snapshot is frozen.
        for (var i = 0; i < 50; i++)
        {
            inspector.Observe(Request(h =>
            {
                h["CF-Connecting-IP"] = "1.2.3.4";
                h["X-Client-TLS-Version"] = "TLSv1.3";
                h["X-JA3-Hash"] = "abc123";
            }));
        }

        var after = inspector.GetSnapshot();
        Assert.NotNull(after);
        Assert.Same(settled, after);
        Assert.True(after!.IsTunnelWithoutEnrichment);
        Assert.False(after.HasForwardedTlsHeaders);
        Assert.False(after.HasJa3OrJa4);
    }

    [Fact]
    public void Disabled_inspector_never_returns_a_snapshot()
    {
        var (inspector, _) = Build(minSamples: 1, enabled: false);
        inspector.Observe(Request(h => h["CF-Connecting-IP"] = "1.2.3.4"));
        Assert.Null(inspector.GetSnapshot());
    }

    private sealed class FakeProxyEnvironment : IProxyEnvironment
    {
        public FakeProxyEnvironment(ProxyTopology topology) => DetectedTopology = topology;
        public ProxyTopology DetectedTopology { get; }
        public string GetRealClientIp(HttpContext httpContext) => "0.0.0.0";
        public string GetRealScheme(HttpContext httpContext) => "https";
    }
}
