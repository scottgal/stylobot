using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class TransportHeaderTrustGateTests
{
    // KnownBrowserFingerprints first entry - when trusted, produces a negative-delta
    // human contribution so the "no human bias when distrusted" assertion is meaningful.
    private const string KnownChromeBrowserHash = "f4c3b2a1e9d8c7b6a5f4e3d2c1b0a9f8";

    private static readonly Mock<IDetectorConfigProvider> ConfigMock = BuildConfigMock();

    private static Mock<IDetectorConfigProvider> BuildConfigMock()
    {
        var mock = new Mock<IDetectorConfigProvider>();
        mock.Setup(c => c.GetDefaults(It.IsAny<string>())).Returns(new DetectorDefaults());
        mock.Setup(c => c.GetManifest(It.IsAny<string>())).Returns((DetectorManifest?)null);
        mock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns((string _, string _, int def) => def);
        mock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>()))
            .Returns((string _, string _, double def) => def);
        mock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Returns((string _, string _, bool def) => def);
        mock.Setup(c => c.GetParameter(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string _, string def) => def);
        return mock;
    }

    private static ITransportHeaderTrust Trust(TransportTrustMode mode)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            TransportTrust = new TransportTrustOptions { Mode = mode }
        });
        return new TransportHeaderTrust(options);
    }

    private static (BlackboardState state, ConcurrentDictionary<string, object> signals) StateFor(
        string peerIp, Action<HttpRequest> setHeaders)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
        setHeaders(ctx.Request);
        var signals = new ConcurrentDictionary<string, object>();

        var state = new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = "test"
        };
        return (state, signals);
    }

    private static TlsFingerprintContributor BuildTls(ITransportHeaderTrust trust)
        => new(NullLogger<TlsFingerprintContributor>.Instance,
               ConfigMock.Object,
               referenceIndex: null,
               transportTrust: trust);

    // Chrome Desktop 90+ SETTINGS value - matched by Http2FingerprintContributor as
    // "Chrome_Desktop_90+" which does NOT contain "Bot" / "HTTP2_Client", so the
    // trusted path produces a negative-delta HumanContribution (ConfidenceDelta = -0.2).
    private const string ChromeDesktopSettings = "1:65536,2:0,3:1000,4:6291456,6:262144";

    private static Http2FingerprintContributor BuildH2(ITransportHeaderTrust trust)
        => new(NullLogger<Http2FingerprintContributor>.Instance,
               ConfigMock.Object,
               norms: new DeploymentNormTracker(),
               transportTrust: trust);

    [Fact]
    public async Task Http2_spoofed_settings_from_public_peer_flags_spoof()
    {
        var (state, signals) = StateFor("203.0.113.9", req =>
        {
            req.Headers["X-HTTP-Protocol"] = "HTTP/2";
            req.Headers["X-HTTP2-Settings"] = ChromeDesktopSettings;
        });
        var sut = BuildH2(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f,
            "Expected transport.spoofed_edge_headers = true for untrusted public peer");
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta < 0);
        // A distrusted peer must NOT be penalised for "missing" header-derived signals
        // we deliberately skipped reading (e.g. the no-stream-priority penalty).
        Assert.DoesNotContain(contributions, c => c.Reason.Contains("stream priority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Http2_settings_from_loopback_peer_is_trusted()
    {
        var (state, signals) = StateFor("127.0.0.1", req =>
        {
            req.Headers["X-HTTP-Protocol"] = "HTTP/2";
            req.Headers["X-HTTP2-Settings"] = ChromeDesktopSettings;
        });
        var sut = BuildH2(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders),
            "Loopback peer should not be flagged as spoofed");
        Assert.True(signals.TryGetValue(SignalKeys.TransportHeadersTrusted, out var t) && (bool)t,
            "Loopback peer headers should be marked trusted");
        Assert.Contains(contributions, c => c.ConfidenceDelta < 0);
    }

    [Fact]
    public async Task Tls_spoofed_ja3_from_public_peer_is_not_trusted_and_flags_spoof()
    {
        // Public peer (203.0.113.x is TEST-NET-3, not loopback/private) sends
        // a known-Chrome JA3 hash. TransportTrustMode.Auto must NOT trust it,
        // must write transport.spoofed_edge_headers = true, and must NOT produce
        // a negative-delta (human) contribution from the hash match.
        var (state, signals) = StateFor("203.0.113.9", req =>
        {
            req.Scheme = "https";
            req.Headers["X-JA3-Hash"] = KnownChromeBrowserHash;
        });
        var sut = BuildTls(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(
            signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f,
            "Expected transport.spoofed_edge_headers = true for untrusted public peer");
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta < 0);
    }

    [Fact]
    public async Task Tls_ja3_from_loopback_peer_is_trusted()
    {
        // Loopback peer is trusted under TransportTrustMode.Auto (TrustPrivatePeers=true).
        // The known-Chrome hash should NOT set spoofed signal and SHOULD set trusted signal.
        var (state, signals) = StateFor("127.0.0.1", req =>
        {
            req.Scheme = "https";
            req.Headers["X-JA3-Hash"] = KnownChromeBrowserHash;
        });
        var sut = BuildTls(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.False(
            signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders),
            "Loopback peer should not be flagged as spoofed");
        Assert.True(
            signals.TryGetValue(SignalKeys.TransportHeadersTrusted, out var t) && (bool)t,
            "Loopback peer headers should be marked trusted");
        Assert.Contains(contributions, c => c.ConfidenceDelta < 0);
    }

    private static Http3FingerprintContributor BuildH3(ITransportHeaderTrust trust)
        => new(NullLogger<Http3FingerprintContributor>.Instance,
               ConfigMock.Object,
               transportTrust: trust);

    [Fact]
    public async Task Http3_spoofed_quic_from_public_peer_flags_spoof()
    {
        var (state, signals) = StateFor("203.0.113.9", req =>
        {
            req.Headers["X-QUIC-Version"] = "1";
            req.Headers["X-QUIC-0RTT"] = "1";
        });
        var sut = BuildH3(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f);
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta < 0);
    }

    [Fact]
    public async Task Http3_quic_from_loopback_peer_is_trusted()
    {
        var (state, signals) = StateFor("127.0.0.1", req =>
        {
            req.Headers["X-QUIC-Version"] = "1";
            req.Headers["X-QUIC-0RTT"] = "1";
        });
        var sut = BuildH3(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders));
        Assert.True(signals.TryGetValue(SignalKeys.TransportHeadersTrusted, out var t) && (bool)t);
    }

    private static TcpIpFingerprintContributor BuildTcp(ITransportHeaderTrust trust)
        => new(NullLogger<TcpIpFingerprintContributor>.Instance,
               ConfigMock.Object,
               transportTrust: trust);

    private static TcpIpFingerprintContributor BuildTcp(ITransportHeaderTrust trust, DeploymentNormTracker norms)
        => new(NullLogger<TcpIpFingerprintContributor>.Instance,
               ConfigMock.Object,
               transportTrust: trust,
               norms: norms);

    // 60 identical observations push the tracker past its 50-request warm-up and pin
    // the bucket rate to 1 (present) or 0 (absent), so Evaluate returns AboveNorm or
    // BelowNorm rather than WarmingUp for the "unknown" UA family the test state uses.
    private static DeploymentNormTracker ConnectionNorm(bool present)
    {
        var norms = new DeploymentNormTracker();
        for (var i = 0; i < 60; i++)
            norms.Record(DeploymentNormTracker.Features.TcpConnectionHeader, "unknown", present: present);
        return norms;
    }

    // ------------------------------------------------------------------
    // Connection-header absence is norm-gated (Signal Assay, f0a25b63): the
    // "missing connection reuse header" penalty only fires where the deployment
    // normally sees the header. Pins the AboveNorm / WarmingUp / BelowNorm branches
    // so the tunnel-Chrome false positive can't regress on the TCP path.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tcp_missing_connection_header_when_norm_present_flags_bot()
    {
        // AboveNorm: this deployment normally sees the Connection header, so a request
        // that omits it (no header on the default request) is genuine bot evidence.
        var (state, _) = StateFor("127.0.0.1", _ => { });
        var sut = BuildTcp(Trust(TransportTrustMode.Auto), ConnectionNorm(present: true));

        var contributions = await sut.ContributeAsync(state);

        Assert.Contains(contributions, c => c.Reason?.Contains("connection reuse") == true);
    }

    [Fact]
    public async Task Tcp_missing_connection_header_during_warmup_does_not_flag()
    {
        // WarmingUp: a cold deployment has too little data to call the header a norm,
        // so absence must fail open (no penalty).
        var (state, _) = StateFor("127.0.0.1", _ => { });
        var sut = BuildTcp(Trust(TransportTrustMode.Auto), new DeploymentNormTracker());

        var contributions = await sut.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c => c.Reason?.Contains("connection reuse") == true);
    }

    [Fact]
    public async Task Tcp_missing_connection_header_when_norm_absent_does_not_flag()
    {
        // BelowNorm: behind a TLS-terminating proxy/tunnel the Connection header is
        // absent for everyone here, so its absence is the norm, not evidence.
        var (state, _) = StateFor("127.0.0.1", _ => { });
        var sut = BuildTcp(Trust(TransportTrustMode.Auto), ConnectionNorm(present: false));

        var contributions = await sut.ContributeAsync(state);

        Assert.DoesNotContain(contributions, c => c.Reason?.Contains("connection reuse") == true);
    }

    [Fact]
    public async Task Tcp_spoofed_os_from_public_peer_flags_spoof()
    {
        var (state, signals) = StateFor("203.0.113.9", req =>
        {
            req.Headers["X-TCP-Window"] = "65535";
            req.Headers["X-TCP-TTL"] = "128";
        });
        var sut = BuildTcp(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f);
    }

    [Fact]
    public async Task Tcp_os_from_loopback_peer_is_trusted()
    {
        var (state, signals) = StateFor("127.0.0.1", req =>
        {
            req.Headers["X-TCP-Window"] = "65535";
            req.Headers["X-TCP-TTL"] = "128";
        });
        var sut = BuildTcp(Trust(TransportTrustMode.Auto));

        await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders));
    }
}
