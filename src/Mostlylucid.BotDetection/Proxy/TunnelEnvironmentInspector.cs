using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>
///     Snapshot of what TLS / fingerprint signals reach the gateway from the
///     observed first batch of requests after process start. Returned by
///     <see cref="ITunnelEnvironmentInspector.GetSnapshot"/>; null until enough
///     samples have been observed to settle the verdict.
/// </summary>
public sealed record TunnelEnvironmentSnapshot(
    ProxyTopology Topology,
    bool BehindProxy,
    bool HasNativeTls,
    bool HasForwardedTlsHeaders,
    bool HasJa3OrJa4,
    int SamplesObserved,
    DateTime SettledAtUtc)
{
    /// <summary>
    ///     True when the gateway sits behind a proxy AND none of the TLS-recovery
    ///     headers (or native TLS handshake feature) are present. This is the
    ///     dashboard-banner trigger -- detection still works, but the TLS-layer
    ///     contributors all write empty signals, so headless-library bots are
    ///     harder to catch.
    /// </summary>
    public bool IsTunnelWithoutEnrichment
        => BehindProxy && !HasNativeTls && !HasForwardedTlsHeaders;

    /// <summary>
    ///     True when TLS metadata reaches the gateway but JA3/JA4 does not. The
    ///     fingerprint surface is partial; the operator can flip on JA3 forwarding
    ///     to close the gap.
    /// </summary>
    public bool IsPartiallyEnriched
        => BehindProxy && (HasNativeTls || HasForwardedTlsHeaders) && !HasJa3OrJa4;
}

/// <summary>
///     Singleton inspector that observes the first N requests after process
///     startup and snapshots whether the gateway is behind a tunnel/proxy AND
///     whether TLS-recovery forwarding (X-Client-TLS-*, X-JA3-*, native TLS
///     handshake) is in place. The snapshot is read by the dashboard to render
///     a "your fingerprint surface is hobbled" banner with a link to the
///     reverse-proxy recipe doc. Re-checks only on process restart -- the
///     deployment shape is static during a run.
/// </summary>
public interface ITunnelEnvironmentInspector
{
    /// <summary>
    ///     Returns the settled snapshot, or null when fewer than
    ///     <c>TunnelEnvironmentOptions.MinSamples</c> requests have been observed.
    ///     The dashboard hides the banner while null.
    /// </summary>
    TunnelEnvironmentSnapshot? GetSnapshot();

    /// <summary>
    ///     Per-request observation hook. Called by the early-pipeline middleware.
    ///     No-op once the inspector has settled, so the steady-state cost is one
    ///     int read.
    /// </summary>
    void Observe(HttpContext httpContext);
}

/// <inheritdoc cref="ITunnelEnvironmentInspector" />
public sealed class TunnelEnvironmentInspector : ITunnelEnvironmentInspector
{
    private readonly IProxyEnvironment _proxy;
    private readonly ILogger<TunnelEnvironmentInspector> _logger;
    private readonly TunnelEnvironmentOptions _options;

    // Lock-free counters incremented on every observed request until the
    // sample target is hit. Once settled, _settled flips and Observe returns
    // immediately on the first check.
    private int _samplesObserved;
    private int _proxyDetectedHits;
    private int _nativeTlsHits;
    private int _forwardedTlsHits;
    private int _ja3OrJa4Hits;
    private int _settled;
    private TunnelEnvironmentSnapshot? _snapshot;

    public TunnelEnvironmentInspector(
        IProxyEnvironment proxy,
        ILogger<TunnelEnvironmentInspector> logger,
        IOptions<BotDetectionOptions> options)
    {
        _proxy = proxy;
        _logger = logger;
        _options = options.Value.TunnelEnvironment;
    }

    public TunnelEnvironmentSnapshot? GetSnapshot()
        => Volatile.Read(ref _settled) == 1 ? _snapshot : null;

    public void Observe(HttpContext httpContext)
    {
        if (!_options.Enabled) return;
        if (Volatile.Read(ref _settled) == 1) return;

        // Trigger the proxy detector against this request's headers so the
        // topology read below reflects the real shape -- the detector is
        // lazy and won't fire on its own until GetRealClientIp is called.
        _ = _proxy.GetRealClientIp(httpContext);

        var headers = httpContext.Request.Headers;
        var topology = _proxy.DetectedTopology;

        // "Behind a proxy" is the union of proxy-topology auto-detect + presence
        // of any X-Forwarded-* / vendor-specific client-IP header. Either one
        // alone is enough -- some proxies emit only one of those signal classes.
        var behindProxy = topology != ProxyTopology.Direct
            || headers.ContainsKey("X-Forwarded-For")
            || headers.ContainsKey("CF-Connecting-IP")
            || headers.ContainsKey("X-Real-IP")
            || headers.ContainsKey("Fastly-Client-IP")
            || headers.ContainsKey("CloudFront-Viewer-Address");
        if (behindProxy) Interlocked.Increment(ref _proxyDetectedHits);

        // Native TLS termination at the gateway: the connection feature is
        // populated by Kestrel only when Kestrel itself did the TLS handshake.
        // Survives only on Direct deployments or "tunnel-in-TCP-mode" shapes.
        if (httpContext.Features.Get<ITlsHandshakeFeature>() is { } tlsFeat
            && tlsFeat.Protocol != System.Security.Authentication.SslProtocols.None)
            Interlocked.Increment(ref _nativeTlsHits);

        // Forwarded TLS metadata from the edge proxy. Any one of these means an
        // operator has wired the proxy recipe (Sb-Http-Version, X-Client-TLS-*,
        // X-Client-TLS-Ext-Sha1) so the TLS-fingerprint contributor has data
        // to work with.
        var hasForwardedTls =
            headers.ContainsKey("X-Client-TLS-Version")
            || headers.ContainsKey("X-Client-TLS-Cipher")
            || headers.ContainsKey("X-Client-TLS-Ext-Sha1")
            || headers.ContainsKey("X-Client-HTTP-Version")
            || headers.ContainsKey("Sb-Http-Version");
        if (hasForwardedTls) Interlocked.Increment(ref _forwardedTlsHits);

        var hasJa3 =
            headers.ContainsKey("X-JA3-Hash")
            || headers.ContainsKey("X-JA3-String")
            || headers.ContainsKey("X-JA4")
            || headers.ContainsKey("X-JA4-Hash");
        if (hasJa3) Interlocked.Increment(ref _ja3OrJa4Hits);

        var samples = Interlocked.Increment(ref _samplesObserved);
        if (samples < _options.MinSamples) return;

        // Settle the snapshot exactly once -- only the first thread to flip
        // _settled from 0 to 1 wins and gets to write the snapshot.
        if (Interlocked.CompareExchange(ref _settled, 1, 0) != 0) return;

        _snapshot = new TunnelEnvironmentSnapshot(
            Topology: topology,
            BehindProxy: _proxyDetectedHits > 0,
            HasNativeTls: _nativeTlsHits > 0,
            HasForwardedTlsHeaders: _forwardedTlsHits > 0,
            HasJa3OrJa4: _ja3OrJa4Hits > 0,
            SamplesObserved: samples,
            SettledAtUtc: DateTime.UtcNow);

        _logger.LogInformation(
            "Tunnel environment inspector settled after {Samples} requests: " +
            "topology={Topology} proxy={Proxy} nativeTls={NativeTls} " +
            "forwardedTls={ForwardedTls} ja3={Ja3}",
            samples, topology, _snapshot.BehindProxy,
            _snapshot.HasNativeTls, _snapshot.HasForwardedTlsHeaders, _snapshot.HasJa3OrJa4);
    }
}
