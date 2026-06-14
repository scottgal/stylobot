# Design: trusted-proxy gate for transport fingerprint headers (G1)

**Date:** 2026-06-13
**Origin:** Gap G1 in [docs/research/2026-06-13-web-scraping-guide-coverage-audit.md](../../research/2026-06-13-web-scraping-guide-coverage-audit.md). Supersedes the standalone memory note `project_x_ja3_header_trust_unverified`.
**Stage:** Stage 0.1 of [docs/research/2026-06-13-coverage-gap-remediation-plan.md](../../research/2026-06-13-coverage-gap-remediation-plan.md) (ships with 0.2/G5).

## Problem

The four transport-fingerprint contributors read edge-injected headers and convert them to human/bot bias with **no check that the request came from a trusted edge**:

- `TlsFingerprintContributor` : `X-JA3-Hash`, `X-JA3-String`, `X-JA4`/`X-JA4-Fingerprint`/`X-JA4-Hash`, `X-Client-TLS-Version`/`X-TLS-Protocol`, `X-Client-TLS-Cipher`/`X-TLS-Cipher`
- `Http2FingerprintContributor` : `X-HTTP-Protocol`, `X-HTTP2-Settings`, `X-HTTP2-Stream-Priority`, `X-HTTP2-Window-Updates`, `X-HTTP2-Push-Enabled`, `X-HTTP2-Preface-Valid`, `X-HTTP2-Pseudoheader-Order`
- `Http3FingerprintContributor` : `X-QUIC-Transport-Params`, `X-QUIC-Version`, `X-QUIC-0RTT`, `X-QUIC-Connection-Migrated`, `X-QUIC-Spin-Bit`, `X-QUIC-Alt-Svc-Used`
- `TcpIpFingerprintContributor` : `X-TCP-Window`, `X-TCP-TTL`, `X-TCP-Options`, `X-TCP-MSS`, `X-IP-DF`, `X-IP-ID-Pattern`

A client reaching the origin directly over HTTPS can set `X-JA3-Hash` to a known-Chrome value and earn `known_browser_fingerprint_confidence: -0.15`, plus matching H2/QUIC/TCP human bonuses, with zero residual evidence of the spoof. This is an inbound spoof of a *human* signal: the most damaging direction. The current `tls.behind_proxy` / `h2.behind_proxy` flags are set on header *presence* alone, so they are themselves spoofable and provide no protection.

Production is documented (REVERSE_PROXY_SIGNALS.md) as always edge-fronted, but nothing in code enforces that the request actually arrived via the edge.

## Goal

Trust transport fingerprint headers only when the request demonstrably came from a trusted edge; otherwise ignore them, fall back to live Kestrel metadata, and emit a weak bot signal. Default-on, without breaking the canonical loopback-fronted production topology.

Non-goals: computing JA3/JA4 from Kestrel directly (out of scope; a direct peer simply loses the JA3 signal rather than getting a spoofed one); changing the edge-side recipes in REVERSE_PROXY_SIGNALS.md beyond documenting the new trust behaviour.

## Design

### New service: `ITransportHeaderTrust`

Single decision point, registered singleton, injected into the four contributors via `ConfiguredContributorBase` (add an optional constructor dependency + a `protected` helper so contributors call `TrustsTransportHeaders(state)` rather than each re-implementing the check).

```csharp
public interface ITransportHeaderTrust
{
    TransportTrustResult Evaluate(BlackboardState state);
}

public readonly record struct TransportTrustResult(
    bool Trusted,
    TransportTrustReason Reason); // Signed, AllowlistedPeer, PrivatePeer, DetectedTopology, UntrustedPublicPeer, GateOff
```

Result is computed once per request and cached on the blackboard (`transport.headers_trusted` bool + `transport.trust_reason`) so all four contributors share one evaluation.

### Decision order (mode `Auto`)

1. **Signed headers.** If a valid HMAC signature over the fingerprint headers is present, trust. Reuse the existing `UpstreamSignatureSecret` / `UpstreamSignatureHeader` plumbing and its constant-time comparison; do not roll new crypto. (Covers untrusted-network hops where peer IP is insufficient.)
2. **Allowlisted peer.** `HttpContext.Connection.RemoteIpAddress` in configured `TrustedProxyIps` (CIDR list) -> trust.
3. **Private peer.** Peer is loopback or RFC1918/RFC4193 private -> trust.
4. **Otherwise** (public direct peer) -> distrust.

Note: the topology-detection arm from the original design was removed during implementation review. `ProxyEnvironmentDetector` infers topology from forgeable headers (X-Forwarded-For, CF-Connecting-IP), so trusting a non-Direct topology let a public peer self-elevate by adding one header. Public-IP edges are trusted only via `TrustedProxyIps`.

`RemoteIpAddress` is the true socket peer because `UseForwardedHeaders` is not enabled in StyloBot hosts; confirm this assumption holds for each host (Gateway, All, Console, Sidecar, Demo) and document that enabling `UseForwardedHeaders` upstream of detection would require adding the LB to `TrustedProxyIps`.

### On distrust

- Skip all `X-*` transport-header reads in the four contributors.
- Fall back to live Kestrel metadata: `ITlsConnectionFeature` (client cert / negotiated protocol where available) and `Request.Protocol` / ALPN for the HTTP version. These describe the real proxy-to-origin (or direct-client) hop and cannot be spoofed by the peer.
- Emit a single weak **bot** contribution keyed `transport.spoofed_edge_headers` *only when distrusted AND at least one gated header was actually present* (a plain direct client sending no edge headers is not penalised). Confidence/weight come from YAML params (new `spoofed_edge_headers_confidence`, `spoofed_edge_headers_weight`), starting low (e.g. 0.3 / 1.2) and tunable. Rationale: a legitimate edge is never a raw public peer injecting these headers.
- Replace the header-presence `behind_proxy` writes with the gate's grounded result, so `tls.behind_proxy` / `h2.behind_proxy` now reflect peer reality.

### Config: `BotDetectionOptions.TransportTrust`

```csharp
public sealed class TransportTrustOptions
{
    public TransportTrustMode Mode { get; set; } = TransportTrustMode.Auto; // Auto | Strict | Off
    public List<string> TrustedProxyIps { get; set; } = []; // CIDRs / IPs
    public bool TrustPrivatePeers { get; set; } = true;       // step 3 private-IP arm
    // Signature reuse: bind to existing UpstreamSignature* settings; no new secret field.
    // Note: TrustDetectedTopology was removed — see decision order note above.
}

public enum TransportTrustMode { Auto, Strict, Off }
```

- **Auto** (default): steps 1-4 as above.
- **Strict**: trust only via step 1 (signed) or step 2 (allowlist). Private arm disabled.
- **Off**: legacy behaviour, trust all headers (escape hatch; logs a one-time Warning at startup that the gate is disabled).

Bound at `BotDetection:TransportTrust`. CIDR parsing reuses any existing helper from `IpContributor` / ASN code; if none, a small `IPNetwork`-based parser validated at startup with a clear error on malformed entries.

## Default-safety argument

The shipped default is `Auto`, not `Off`. The only behaviour change versus today is for a **public direct peer that sends gated headers** : exactly the attack, never a legitimate pattern. The canonical production topology (Internet -> Cloudflare Tunnel -> Caddy (TLS) -> YARP Gateway) presents a loopback/private peer to the gateway, which `Auto` trusts via step 3, so stylobot.net and equivalent edge-fronted deploys are unaffected. Deployments with a public-IP load balancer in front (e.g. AWS ALB on a routable address) add it to `TrustedProxyIps`; this is the documented migration note.

## Affected files

- New: `Proxy/ITransportHeaderTrust.cs`, `Proxy/TransportHeaderTrust.cs`, `Models/TransportTrustOptions.cs` (+ enum).
- `Models/BotDetectionOptions.cs` : add `TransportTrust` node + binding.
- `Orchestration/ContributingDetectors/ConfiguredContributorBase.cs` : optional `ITransportHeaderTrust` dep + `protected TrustsTransportHeaders(state)` helper writing the shared blackboard flags.
- `TlsFingerprintContributor.cs`, `Http2FingerprintContributor.cs`, `Http3FingerprintContributor.cs`, `TcpIpFingerprintContributor.cs` : gate header reads; fold `behind_proxy`; add distrust contribution + YAML params.
- The four `*.detector.yaml` manifests : add `spoofed_edge_headers_confidence` / `_weight` defaults.
- `Extensions/ServiceCollectionExtensions.cs` : register `ITransportHeaderTrust`.
- `Models/DetectionContext.cs` SignalKeys : `TransportHeadersTrusted`, `TransportTrustReason`, `TransportSpoofedEdgeHeaders`.
- `Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs` : narrative entry for the new signal.
- `docs/REVERSE_PROXY_SIGNALS.md` : document the gate, the `TrustedProxyIps` requirement for public-IP edges, and the `Off` escape hatch.

## Testing

- Unit (`ITransportHeaderTrust`): signed-valid, signed-invalid, allowlisted peer, loopback peer, private peer, known-topology peer, public direct peer; Strict and Off modes.
- Contributor tests: per contributor, spoofed `X-*` header from an untrusted public peer is ignored, no human bias applied, `transport.spoofed_edge_headers` set; same header from a loopback peer is honoured exactly as today (regression).
- Orchestration/BDF: a direct-peer spoofed-Chrome-JA3 request scores as bot, not human (closes the audit's central exploit). Loopback-fronted human scenarios unchanged (no false-positive regression).
- BDF cloak rig: after this + G5, re-run damru/Multilogin scenarios and record the score movement off 0.07.

## Open questions resolved

- *Ignore vs penalise untrusted headers?* Penalise weakly (`transport.spoofed_edge_headers`), only when gated headers were actually present. Settled.
- *Default mode?* `Auto`. Settled (approved 2026-06-13).
