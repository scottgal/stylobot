using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Proxy;

/// <inheritdoc />
public sealed class TransportHeaderTrust : ITransportHeaderTrust
{
    private readonly IOptions<BotDetectionOptions> _options;
    private readonly IProxyEnvironment? _proxyEnv;

    public TransportHeaderTrust(IOptions<BotDetectionOptions> options, IProxyEnvironment? proxyEnv = null)
    {
        _options = options;
        _proxyEnv = proxyEnv;
    }

    public TransportTrustResult Evaluate(BlackboardState state)
    {
        var result = Decide(state.HttpContext);
        state.WriteSignal(SignalKeys.TransportHeadersTrusted, result.Trusted);
        state.WriteSignal(SignalKeys.TransportTrustReason, result.Reason);
        return result;
    }

    /// <summary>Pure decision logic (no signal writes), exposed for testing.</summary>
    public TransportTrustResult Decide(HttpContext ctx)
    {
        var opts = _options.Value.TransportTrust;
        if (opts.Mode == TransportTrustMode.Off)
            return new TransportTrustResult(true, "GateOff");

        var peer = ctx.Connection?.RemoteIpAddress;

        // Allowlist applies in both Auto and Strict.
        if (peer is not null && opts.TrustedProxyIps.Count > 0)
        {
            foreach (var cidr in opts.TrustedProxyIps)
            {
                if (CidrHelper.IsInSubnet(peer, cidr))
                    return new TransportTrustResult(true, "AllowlistedPeer");
            }
        }

        if (opts.Mode == TransportTrustMode.Strict)
            return new TransportTrustResult(false, "UntrustedPublicPeer");

        // Auto: loopback / private peer (the loopback-fronted production topology).
        if (opts.TrustPrivatePeers && NetworkHelper.IsLocalIp(peer))
            return new TransportTrustResult(true, "PrivatePeer");

        // Auto: a known edge topology was detected.
        if (opts.TrustDetectedTopology && _proxyEnv is not null)
        {
            // GetRealClientIp triggers one-time topology auto-detection.
            try { _proxyEnv.GetRealClientIp(ctx); } catch { /* detection best-effort */ }
            if (_proxyEnv.DetectedTopology != ProxyTopology.Direct)
                return new TransportTrustResult(true, "DetectedTopology");
        }

        return new TransportTrustResult(false, "UntrustedPublicPeer");
    }
}
