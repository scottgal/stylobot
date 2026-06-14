# Transport Header Trust Gate (G1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the four transport-fingerprint contributors from trusting edge-injected headers (`X-JA3-*`, `X-Client-TLS-*`, `X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`) unless the request demonstrably arrived from a trusted edge; otherwise ignore those headers and emit a weak bot signal.

**Architecture:** A single stateless `ITransportHeaderTrust` service decides, per request, whether transport headers are trustworthy based on the immediate TCP peer (`HttpContext.Connection.RemoteIpAddress`): trusted if the peer is on a configured allowlist, is loopback/private, or the detected proxy topology is a known edge. The four contributors inject the service, prefix each `X-*` header read with the trust verdict, and add a weak `transport.spoofed_edge_headers` bot contribution when an untrusted public peer sent gated headers. Default mode is `Auto` (closes the hole for public direct peers without breaking the loopback-fronted production topology).

**Tech Stack:** C# / .NET 10, ASP.NET Core, xUnit + Moq, existing helpers `CidrHelper`, `NetworkHelper`, `IProxyEnvironment`.

**Spec:** [docs/superpowers/specs/2026-06-13-transport-header-trust-gate-design.md](../specs/2026-06-13-transport-header-trust-gate-design.md)

## Scope note (read first)

The design lists an HMAC-signed-header arm as decision step 1. **That arm is deferred out of this plan** and captured as a follow-up: no proxy in the fleet emits a transport-header signature today, so building and testing a crypto path with no producer is YAGNI for the first cut. This plan ships the peer-IP gate (allowlist + private/loopback + detected-topology), which delivers all the security value for the documented topologies. The `Off` escape hatch and the existing `UpstreamSignature*` config remain untouched, so the signature arm can be added later without breaking anything. `Strict` mode therefore means "allowlist only" in this plan.

## File structure

| File | Responsibility | Action |
|---|---|---|
| `src/Mostlylucid.BotDetection/Models/TransportTrustOptions.cs` | `TransportTrustOptions` class + `TransportTrustMode` enum | Create |
| `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` | Add `TransportTrust` property | Modify |
| `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` | 3 new `SignalKeys` constants | Modify |
| `src/Mostlylucid.BotDetection/Proxy/ITransportHeaderTrust.cs` | Interface + `TransportTrustResult` record | Create |
| `src/Mostlylucid.BotDetection/Proxy/TransportHeaderTrust.cs` | Decision logic (`Decide` pure + `Evaluate` writes signals) | Create |
| `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` | Register the service | Modify |
| `.../ContributingDetectors/TlsFingerprintContributor.cs` | Inject + gate TLS/JA3 header reads | Modify |
| `.../ContributingDetectors/Http2FingerprintContributor.cs` | Inject + gate H2 header reads | Modify |
| `.../ContributingDetectors/Http3FingerprintContributor.cs` | Inject + gate QUIC header reads | Modify |
| `.../ContributingDetectors/TcpIpFingerprintContributor.cs` | Inject + gate TCP/IP header reads | Modify |
| `.../Manifests/detectors/{tls,http2,http3,tcpip}.detector.yaml` | Tunable params | Modify |
| `src/Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs` | Narrative entry | Modify |
| `docs/REVERSE_PROXY_SIGNALS.md` | Document the gate | Modify |
| `src/Mostlylucid.BotDetection.Test/Proxy/TransportHeaderTrustTests.cs` | Service unit tests | Create |
| `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs` | Per-contributor gate tests | Create |

---

### Task 1: Options model

**Files:**
- Create: `src/Mostlylucid.BotDetection/Models/TransportTrustOptions.cs`
- Modify: `src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs` (add property near the existing `ProxyEnvironment` property at line ~178)

- [ ] **Step 1: Create the options type**

```csharp
namespace Mostlylucid.BotDetection.Models;

/// <summary>How transport fingerprint headers (X-JA3-*, X-Client-TLS-*, X-HTTP2-*, X-QUIC-*, X-TCP-*) are trusted.</summary>
public enum TransportTrustMode
{
    /// <summary>Trust if peer is allowlisted, loopback/private, or detected topology is a known edge. Default.</summary>
    Auto,

    /// <summary>Trust only if the immediate peer is in TrustedProxyIps.</summary>
    Strict,

    /// <summary>Trust all transport headers regardless of peer (legacy behaviour; logs a startup warning).</summary>
    Off
}

/// <summary>
/// Controls whether edge-injected transport fingerprint headers are trusted.
/// Bound at BotDetection:TransportTrust.
/// </summary>
public sealed class TransportTrustOptions
{
    /// <summary>Trust mode. Default Auto.</summary>
    public TransportTrustMode Mode { get; set; } = TransportTrustMode.Auto;

    /// <summary>CIDRs / IPs of trusted reverse proxies, e.g. ["10.0.0.0/8", "203.0.113.5"].</summary>
    public List<string> TrustedProxyIps { get; set; } = [];

    /// <summary>Auto mode: trust headers when IProxyEnvironment detected a known edge topology (not Direct). Default true.</summary>
    public bool TrustDetectedTopology { get; set; } = true;

    /// <summary>Auto mode: trust headers when the immediate peer is loopback or RFC1918/RFC4193 private. Default true.</summary>
    public bool TrustPrivatePeers { get; set; } = true;
}
```

- [ ] **Step 2: Wire it onto BotDetectionOptions**

In `BotDetectionOptions.cs`, immediately after the existing `ProxyEnvironment` property (around line 178), add:

```csharp
    /// <summary>Trusted-proxy gate for edge-injected transport fingerprint headers (G1).</summary>
    public TransportTrustOptions TransportTrust { get; set; } = new();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/TransportTrustOptions.cs src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs
git commit -m "feat(transport-trust): add TransportTrustOptions config model"
```

---

### Task 2: Signal keys

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Models/DetectionContext.cs` (the `SignalKeys` class starts at line ~203; add near the existing `transport.*` keys around line 1175)

- [ ] **Step 1: Add the three constants**

In the `SignalKeys` class, grouped with the other `transport.*` keys, add:

```csharp
    // ===== Transport header trust (G1) =====

    /// <summary>bool: whether edge transport fingerprint headers were trusted for this request.</summary>
    public const string TransportHeadersTrusted = "transport.headers_trusted";

    /// <summary>string: reason for the trust verdict (AllowlistedPeer, PrivatePeer, DetectedTopology, UntrustedPublicPeer, GateOff).</summary>
    public const string TransportTrustReason = "transport.trust_reason";

    /// <summary>bool: an untrusted direct peer sent edge transport fingerprint headers (possible spoof).</summary>
    public const string TransportSpoofedEdgeHeaders = "transport.spoofed_edge_headers";
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Models/DetectionContext.cs
git commit -m "feat(transport-trust): add transport trust signal keys"
```

---

### Task 3: The trust service (core logic, TDD)

**Files:**
- Create: `src/Mostlylucid.BotDetection/Proxy/ITransportHeaderTrust.cs`
- Create: `src/Mostlylucid.BotDetection/Proxy/TransportHeaderTrust.cs`
- Test: `src/Mostlylucid.BotDetection.Test/Proxy/TransportHeaderTrustTests.cs`

- [ ] **Step 1: Create the interface + result record**

```csharp
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>Outcome of a transport-header trust evaluation.</summary>
public readonly record struct TransportTrustResult(bool Trusted, string Reason);

/// <summary>
/// Decides whether edge-injected transport fingerprint headers should be trusted,
/// based on the immediate TCP peer and configured policy.
/// </summary>
public interface ITransportHeaderTrust
{
    /// <summary>Evaluate trust and write transport.headers_trusted / transport.trust_reason signals.</summary>
    TransportTrustResult Evaluate(BlackboardState state);
}
```

- [ ] **Step 2: Write the failing tests**

```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Proxy;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Proxy;

public class TransportHeaderTrustTests
{
    private static TransportHeaderTrust Build(TransportTrustOptions opts, ProxyTopology topology = ProxyTopology.Direct)
    {
        var options = Options.Create(new BotDetectionOptions { TransportTrust = opts });
        var env = new Mock<IProxyEnvironment>();
        env.SetupGet(e => e.DetectedTopology).Returns(topology);
        env.Setup(e => e.GetRealClientIp(It.IsAny<HttpContext>())).Returns("1.2.3.4");
        return new TransportHeaderTrust(options, env.Object);
    }

    private static HttpContext Ctx(string peerIp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
        return ctx;
    }

    [Fact]
    public void Off_mode_trusts_any_peer()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Off });
        var r = sut.Decide(Ctx("203.0.113.9"));
        Assert.True(r.Trusted);
        Assert.Equal("GateOff", r.Reason);
    }

    [Fact]
    public void Auto_trusts_loopback_peer()
    {
        var sut = Build(new TransportTrustOptions());
        Assert.True(sut.Decide(Ctx("127.0.0.1")).Trusted);
    }

    [Fact]
    public void Auto_trusts_private_peer()
    {
        var sut = Build(new TransportTrustOptions());
        Assert.Equal("PrivatePeer", sut.Decide(Ctx("10.0.0.5")).Reason);
    }

    [Fact]
    public void Auto_distrusts_public_direct_peer()
    {
        var sut = Build(new TransportTrustOptions());
        var r = sut.Decide(Ctx("203.0.113.9"));
        Assert.False(r.Trusted);
        Assert.Equal("UntrustedPublicPeer", r.Reason);
    }

    [Fact]
    public void Auto_trusts_detected_edge_topology()
    {
        var sut = Build(new TransportTrustOptions(), ProxyTopology.Cloudflare);
        var r = sut.Decide(Ctx("203.0.113.9"));
        Assert.True(r.Trusted);
        Assert.Equal("DetectedTopology", r.Reason);
    }

    [Fact]
    public void Allowlisted_public_peer_is_trusted()
    {
        var sut = Build(new TransportTrustOptions { TrustedProxyIps = ["203.0.113.0/24"] });
        Assert.Equal("AllowlistedPeer", sut.Decide(Ctx("203.0.113.9")).Reason);
    }

    [Fact]
    public void Strict_mode_distrusts_private_peer_without_allowlist()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Strict });
        Assert.False(sut.Decide(Ctx("10.0.0.5")).Trusted);
    }

    [Fact]
    public void Strict_mode_trusts_allowlisted_peer()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Strict, TrustedProxyIps = ["10.0.0.0/8"] });
        Assert.True(sut.Decide(Ctx("10.0.0.5")).Trusted);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~TransportHeaderTrustTests"`
Expected: FAIL to compile ("TransportHeaderTrust does not exist" / "Decide not found").

- [ ] **Step 4: Implement the service**

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Test/ --filter "FullyQualifiedName~TransportHeaderTrustTests"`
Expected: PASS (8 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Mostlylucid.BotDetection/Proxy/ITransportHeaderTrust.cs src/Mostlylucid.BotDetection/Proxy/TransportHeaderTrust.cs src/Mostlylucid.BotDetection.Test/Proxy/TransportHeaderTrustTests.cs
git commit -m "feat(transport-trust): peer-IP trust decision service with tests"
```

---

### Task 4: DI registration

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (near line 463 where `IProxyEnvironment` is registered)

- [ ] **Step 1: Register the service**

Immediately after the existing `services.TryAddSingleton<IProxyEnvironment, ProxyEnvironmentDetector>();` line, add:

```csharp
        services.TryAddSingleton<ITransportHeaderTrust, TransportHeaderTrust>();
```

Ensure `using Mostlylucid.BotDetection.Proxy;` is present at the top of the file (it already references `IProxyEnvironment` from that namespace, so it should be).

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs
git commit -m "feat(transport-trust): register ITransportHeaderTrust"
```

---

### Task 5: Gate TlsFingerprintContributor

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/TlsFingerprintContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs`

- [ ] **Step 1: Write the failing test**

Create the test file with the first contributor's cases. Use the project's existing `BlackboardState` construction pattern (see `Unit/InconsistencyContributorTests.cs` for the helper shape).

```csharp
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Proxy;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

public class TransportHeaderTrustGateTests
{
    private static ITransportHeaderTrust Trust(TransportTrustMode mode)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            TransportTrust = new TransportTrustOptions { Mode = mode }
        });
        return new TransportHeaderTrust(options, proxyEnv: null);
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
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = [],
            RequestId = "test",
            SignalWriter = signals
        };
        return (state, signals);
    }

    private static TlsFingerprintContributor BuildTls(ITransportHeaderTrust trust)
        => new(NullLogger<TlsFingerprintContributor>.Instance,
               new TestDetectorConfigProvider(), referenceIndex: null, transportTrust: trust);

    [Fact]
    public async Task Tls_spoofed_ja3_from_public_peer_is_not_trusted_and_flags_spoof()
    {
        var (state, signals) = StateFor("203.0.113.9", req =>
            req.Headers["X-JA3-Hash"] = "cd08e31494f9531f560d64c695473da9"); // a known-Chrome hash
        var sut = BuildTls(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f);
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta < 0); // no human bias earned
    }

    [Fact]
    public async Task Tls_ja3_from_loopback_peer_is_trusted()
    {
        var (state, signals) = StateFor("127.0.0.1", req =>
            req.Headers["X-JA3-Hash"] = "cd08e31494f9531f560d64c695473da9");
        var sut = BuildTls(Trust(TransportTrustMode.Auto));

        await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders));
        Assert.True(signals.TryGetValue(SignalKeys.TransportHeadersTrusted, out var t) && (bool)t);
    }
}
```

Note: `TestDetectorConfigProvider` is the existing test config provider used by other contributor tests in this project; reuse it. If its type name differs, match the one `InconsistencyContributorTests` uses. The `cd08...` value is a placeholder hash string; replace it with any value present in `TlsFingerprintContributor`'s `KnownBrowserFingerprints` set so the "human bias" path would otherwise fire.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Tls"`
Expected: FAIL to compile (`transportTrust` parameter does not exist on the constructor).

- [ ] **Step 3: Add the constructor dependency**

In `TlsFingerprintContributor.cs`, add a field and an optional constructor parameter (keeps existing call sites working):

```csharp
    private readonly ITransportHeaderTrust? _transportTrust;
```

Change the constructor (currently `logger, configProvider, IJa3ReferenceIndex? referenceIndex = null`) to:

```csharp
    public TlsFingerprintContributor(
        ILogger<TlsFingerprintContributor> logger,
        IDetectorConfigProvider configProvider,
        IJa3ReferenceIndex? referenceIndex = null,
        ITransportHeaderTrust? transportTrust = null)
        : base(configProvider)
    {
        _logger = logger;
        _referenceIndex = referenceIndex;
        _transportTrust = transportTrust;
    }
```

Add `using Mostlylucid.BotDetection.Proxy;` if not present.

- [ ] **Step 4: Add the gate at the top of ContributeAsync**

Inside `ContributeAsync`, immediately after `var contributions = new List<DetectionContribution>();`, add:

```csharp
        var req = state.HttpContext.Request;
        var trust = _transportTrust?.Evaluate(state);
        var trustHeaders = trust?.Trusted ?? true;

        var gatedHeaderPresent =
            req.Headers.ContainsKey("X-JA3-Hash") || req.Headers.ContainsKey("X-JA3-String") ||
            req.Headers.ContainsKey("X-JA4") || req.Headers.ContainsKey("X-JA4-Fingerprint") ||
            req.Headers.ContainsKey("X-JA4-Hash") || req.Headers.ContainsKey("X-Client-TLS-Version") ||
            req.Headers.ContainsKey("X-TLS-Protocol") || req.Headers.ContainsKey("X-Client-TLS-Cipher") ||
            req.Headers.ContainsKey("X-TLS-Cipher");

        if (trust is { Trusted: false } && gatedHeaderPresent)
        {
            state.WriteSignal(SignalKeys.TransportSpoofedEdgeHeaders, true);
            contributions.Add(BotContribution(
                "TLS",
                "Edge TLS fingerprint headers from an untrusted direct peer (possible spoof)",
                confidenceOverride: GetParam("spoofed_edge_headers_confidence", 0.3),
                weightMultiplier: GetParam("spoofed_edge_headers_weight", 1.2),
                botType: BotType.Scraper.ToString()));
        }
```

- [ ] **Step 5: Gate every X-* read in this file**

Prefix the read condition of each of these headers with `trustHeaders && ` so an untrusted peer's values are never consumed: `X-Client-TLS-Version`, `X-TLS-Protocol`, `X-Client-TLS-Cipher`, `X-TLS-Cipher`, `X-JA3-Hash`, `X-JA3-String`, `X-JA4`, `X-JA4-Fingerprint`, `X-JA4-Hash`. For example, change:

```csharp
        if (state.HttpContext.Request.Headers.TryGetValue("X-JA3-Hash", out var ja3HashHeader))
```

to:

```csharp
        if (trustHeaders && state.HttpContext.Request.Headers.TryGetValue("X-JA3-Hash", out var ja3HashHeader))
```

Leave `X-Forwarded-Proto` (scheme detection) untouched; it does not feed fingerprint bias.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Tls"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/TlsFingerprintContributor.cs src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs
git commit -m "feat(transport-trust): gate TLS/JA3 header reads behind peer trust"
```

---

### Task 6: Gate Http2FingerprintContributor

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/Http2FingerprintContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs` (append)

- [ ] **Step 1: Write the failing test (append to the test class)**

```csharp
    private static Http2FingerprintContributor BuildH2(ITransportHeaderTrust trust)
        => new(NullLogger<Http2FingerprintContributor>.Instance,
               new TestDetectorConfigProvider(), transportTrust: trust);

    [Fact]
    public async Task Http2_spoofed_settings_from_public_peer_flags_spoof()
    {
        var (state, signals) = StateFor("203.0.113.9", req =>
        {
            req.Headers["X-HTTP-Protocol"] = "HTTP/2";
            req.Headers["X-HTTP2-Settings"] = "1:65536,2:0,3:1000,4:6291456,6:262144";
        });
        var sut = BuildH2(Trust(TransportTrustMode.Auto));

        var contributions = await sut.ContributeAsync(state);

        Assert.True(signals.TryGetValue(SignalKeys.TransportSpoofedEdgeHeaders, out var f) && (bool)f);
        Assert.DoesNotContain(contributions, c => c.ConfidenceDelta < 0);
    }

    [Fact]
    public async Task Http2_settings_from_loopback_peer_is_trusted()
    {
        var (state, signals) = StateFor("127.0.0.1", req =>
        {
            req.Headers["X-HTTP-Protocol"] = "HTTP/2";
            req.Headers["X-HTTP2-Settings"] = "1:65536,2:0,3:1000,4:6291456,6:262144";
        });
        var sut = BuildH2(Trust(TransportTrustMode.Auto));

        await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Http2"`
Expected: FAIL to compile (`transportTrust` parameter does not exist).

- [ ] **Step 3: Add the constructor dependency**

Add field `private readonly ITransportHeaderTrust? _transportTrust;`, add `ITransportHeaderTrust? transportTrust = null` as the last constructor parameter, assign `_transportTrust = transportTrust;` in the body, and add `using Mostlylucid.BotDetection.Proxy;`.

- [ ] **Step 4: Add the gate at the top of ContributeAsync**

After the contributions list is created, add:

```csharp
        var req = state.HttpContext.Request;
        var trust = _transportTrust?.Evaluate(state);
        var trustHeaders = trust?.Trusted ?? true;

        var gatedHeaderPresent =
            req.Headers.ContainsKey("X-HTTP-Protocol") || req.Headers.ContainsKey("X-HTTP2-Settings") ||
            req.Headers.ContainsKey("X-HTTP2-Stream-Priority") || req.Headers.ContainsKey("X-HTTP2-Window-Updates") ||
            req.Headers.ContainsKey("X-HTTP2-Push-Enabled") || req.Headers.ContainsKey("X-HTTP2-Preface-Valid") ||
            req.Headers.ContainsKey("X-HTTP2-Pseudoheader-Order");

        if (trust is { Trusted: false } && gatedHeaderPresent)
        {
            state.WriteSignal(SignalKeys.TransportSpoofedEdgeHeaders, true);
            contributions.Add(BotContribution(
                "HTTP2",
                "Edge HTTP/2 fingerprint headers from an untrusted direct peer (possible spoof)",
                confidenceOverride: GetParam("spoofed_edge_headers_confidence", 0.3),
                weightMultiplier: GetParam("spoofed_edge_headers_weight", 1.2),
                botType: BotType.Scraper.ToString()));
        }
```

- [ ] **Step 5: Gate every X-* read in this file**

Prefix the read condition of each with `trustHeaders && `: `X-HTTP-Protocol`, `X-HTTP2-Settings`, `X-HTTP2-Stream-Priority`, `X-HTTP2-Window-Updates`, `X-HTTP2-Push-Enabled`, `X-HTTP2-Preface-Valid`, `X-HTTP2-Pseudoheader-Order`. The current `behindProxy` flag (set from `X-HTTP-Protocol` presence) now lives inside the `trustHeaders` guard, so `h2.behind_proxy` reflects a trusted proxy rather than mere header presence.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Http2"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/Http2FingerprintContributor.cs src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs
git commit -m "feat(transport-trust): gate HTTP/2 header reads behind peer trust"
```

---

### Task 7: Gate Http3FingerprintContributor

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/Http3FingerprintContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs` (append)

- [ ] **Step 1: Write the failing test (append)**

```csharp
    private static Http3FingerprintContributor BuildH3(ITransportHeaderTrust trust)
        => new(NullLogger<Http3FingerprintContributor>.Instance,
               new TestDetectorConfigProvider(), transportTrust: trust);

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

        await sut.ContributeAsync(state);

        Assert.False(signals.ContainsKey(SignalKeys.TransportSpoofedEdgeHeaders));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Http3"`
Expected: FAIL to compile.

- [ ] **Step 3: Add the constructor dependency**

Add field `private readonly ITransportHeaderTrust? _transportTrust;`, add `ITransportHeaderTrust? transportTrust = null` as the last constructor parameter, assign it, add `using Mostlylucid.BotDetection.Proxy;`.

- [ ] **Step 4: Add the gate at the top of ContributeAsync**

```csharp
        var req = state.HttpContext.Request;
        var trust = _transportTrust?.Evaluate(state);
        var trustHeaders = trust?.Trusted ?? true;

        var gatedHeaderPresent =
            req.Headers.ContainsKey("X-QUIC-Transport-Params") || req.Headers.ContainsKey("X-QUIC-Version") ||
            req.Headers.ContainsKey("X-QUIC-0RTT") || req.Headers.ContainsKey("X-QUIC-Connection-Migrated") ||
            req.Headers.ContainsKey("X-QUIC-Spin-Bit") || req.Headers.ContainsKey("X-QUIC-Alt-Svc-Used");

        if (trust is { Trusted: false } && gatedHeaderPresent)
        {
            state.WriteSignal(SignalKeys.TransportSpoofedEdgeHeaders, true);
            contributions.Add(BotContribution(
                "HTTP3",
                "Edge QUIC fingerprint headers from an untrusted direct peer (possible spoof)",
                confidenceOverride: GetParam("spoofed_edge_headers_confidence", 0.3),
                weightMultiplier: GetParam("spoofed_edge_headers_weight", 1.2),
                botType: BotType.Scraper.ToString()));
        }
```

- [ ] **Step 5: Gate every X-* read in this file**

Prefix with `trustHeaders && `: `X-QUIC-Transport-Params`, `X-QUIC-Version`, `X-QUIC-0RTT`, `X-QUIC-Connection-Migrated`, `X-QUIC-Spin-Bit`, `X-QUIC-Alt-Svc-Used`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Http3"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/Http3FingerprintContributor.cs src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs
git commit -m "feat(transport-trust): gate QUIC header reads behind peer trust"
```

---

### Task 8: Gate TcpIpFingerprintContributor

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/TcpIpFingerprintContributor.cs`
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs` (append)

- [ ] **Step 1: Write the failing test (append)**

```csharp
    private static TcpIpFingerprintContributor BuildTcp(ITransportHeaderTrust trust)
        => new(NullLogger<TcpIpFingerprintContributor>.Instance,
               new TestDetectorConfigProvider(), transportTrust: trust);

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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Tcp"`
Expected: FAIL to compile.

- [ ] **Step 3: Add the constructor dependency**

Add field `private readonly ITransportHeaderTrust? _transportTrust;`, add `ITransportHeaderTrust? transportTrust = null` as the last constructor parameter, assign it, add `using Mostlylucid.BotDetection.Proxy;`.

- [ ] **Step 4: Add the gate at the top of ContributeAsync**

```csharp
        var req = state.HttpContext.Request;
        var trust = _transportTrust?.Evaluate(state);
        var trustHeaders = trust?.Trusted ?? true;

        var gatedHeaderPresent =
            req.Headers.ContainsKey("X-TCP-Window") || req.Headers.ContainsKey("X-TCP-TTL") ||
            req.Headers.ContainsKey("X-TCP-Options") || req.Headers.ContainsKey("X-TCP-MSS") ||
            req.Headers.ContainsKey("X-IP-DF") || req.Headers.ContainsKey("X-IP-ID-Pattern");

        if (trust is { Trusted: false } && gatedHeaderPresent)
        {
            state.WriteSignal(SignalKeys.TransportSpoofedEdgeHeaders, true);
            contributions.Add(BotContribution(
                "TCPIP",
                "Edge TCP/IP fingerprint headers from an untrusted direct peer (possible spoof)",
                confidenceOverride: GetParam("spoofed_edge_headers_confidence", 0.3),
                weightMultiplier: GetParam("spoofed_edge_headers_weight", 1.2),
                botType: BotType.Scraper.ToString()));
        }
```

- [ ] **Step 5: Gate every X-* read in this file**

Prefix with `trustHeaders && `: `X-TCP-Window`, `X-TCP-TTL`, `X-TCP-Options`, `X-TCP-MSS`, `X-IP-DF`, `X-IP-ID-Pattern`. Leave the plain `Connection` header read (it is a real HTTP header, not edge-injected).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportHeaderTrustGateTests.Tcp"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/ContributingDetectors/TcpIpFingerprintContributor.cs src/Mostlylucid.BotDetection.Orchestration.Tests/Unit/TransportHeaderTrustGateTests.cs
git commit -m "feat(transport-trust): gate TCP/IP header reads behind peer trust"
```

---

### Task 9: YAML tunable parameters

**Files:**
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/tls.detector.yaml`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/http2.detector.yaml`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/http3.detector.yaml`
- Modify: `src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/tcpip.detector.yaml`

- [ ] **Step 1: Add params to each manifest**

Under the existing `defaults: parameters:` block of each of the four manifests, add:

```yaml
      spoofed_edge_headers_confidence: 0.3
      spoofed_edge_headers_weight: 1.2
```

(Match the existing indentation in each file. The code already defaults to these values via `GetParam`, so this only exposes them for tuning. If a manifest filename differs from the list above, confirm via `ls src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/`.)

- [ ] **Step 2: Build (manifests are embedded resources)**

Run: `dotnet build src/Mostlylucid.BotDetection/Mostlylucid.BotDetection.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/tls.detector.yaml src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/http2.detector.yaml src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/http3.detector.yaml src/Mostlylucid.BotDetection/Orchestration/Manifests/detectors/tcpip.detector.yaml
git commit -m "feat(transport-trust): expose spoofed-edge-header params in manifests"
```

---

### Task 10: Narrative, docs, and Off-mode startup warning

**Files:**
- Modify: `src/Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`
- Modify: `docs/REVERSE_PROXY_SIGNALS.md`
- Modify: `src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs` (startup warning)

- [ ] **Step 1: Add the narrative entry**

In `DetectionNarrativeBuilder.cs`, add to the `DetectorFriendlyNames` dictionary and `DetectorCategories` dictionary an entry for the spoofed-edge-header signal, following the existing pattern in that file (key on the contributor `Name` values already present: TLS / HTTP2 / HTTP3 / TCPIP fingerprint detectors). If the narrative keys are signal-based, add a friendly description for `transport.spoofed_edge_headers`: "Edge fingerprint headers from an untrusted peer (possible spoof)".

- [ ] **Step 2: Document the gate**

Append a section to `docs/REVERSE_PROXY_SIGNALS.md`:

```markdown
## Trusted-proxy gate (transport fingerprint headers)

The transport fingerprint headers documented above (`X-JA3-*`, `X-Client-TLS-*`,
`X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*`) are only trusted when the request demonstrably
arrived via a trusted edge. This prevents a client reaching the origin directly
from spoofing a known-browser fingerprint to earn a human bias.

Configured at `BotDetection:TransportTrust`:

- `Mode` : `Auto` (default), `Strict`, or `Off`.
  - **Auto** trusts these headers when the immediate peer is loopback/private,
    on `TrustedProxyIps`, or the detected proxy topology is a known edge
    (Cloudflare/CloudFront/Fastly/nginx). This matches the canonical
    `cloudflared -> Caddy -> gateway` topology, where the gateway's peer is loopback.
  - **Strict** trusts only peers in `TrustedProxyIps`.
  - **Off** restores the legacy behaviour (trust all; logs a startup warning).
- `TrustedProxyIps` : CIDRs/IPs of your reverse proxies. **Required** if a
  public-IP load balancer (e.g. AWS ALB on a routable address) sits in front,
  because Auto distrusts public peers by default.

When headers are distrusted, the gateway ignores them, falls back to live Kestrel
TLS/protocol metadata, and emits a weak `transport.spoofed_edge_headers` bot signal
only when such headers were actually present.
```

- [ ] **Step 3: Add the Off-mode startup warning**

In `ServiceCollectionExtensions.cs`, in the same area where other options-validation warnings are logged at startup (search for an existing `ILogger`/validation block; if none in this method, place it where `AddBotDetection` finalizes options), add a check that logs once when `TransportTrust.Mode == Off`:

```csharp
        // Warn when the transport-header trust gate is disabled.
        // (Mirror the existing startup-validation logging pattern in this file.)
```

If `ServiceCollectionExtensions` has no startup-logging hook, instead add this validation to `BotDetectionOptions.Validate(...)` near the existing `TrustUpstreamDetection` warning at line ~3693, appending a warning string when `options.TransportTrust.Mode == TransportTrustMode.Off`. Use the same warning-collection mechanism already used there.

- [ ] **Step 4: Build**

Run: `dotnet build mostlylucid.stylobot.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs docs/REVERSE_PROXY_SIGNALS.md src/Mostlylucid.BotDetection/Extensions/ServiceCollectionExtensions.cs src/Mostlylucid.BotDetection/Models/BotDetectionOptions.cs
git commit -m "docs(transport-trust): narrative entry, reverse-proxy docs, off-mode warning"
```

---

### Task 11: Integration regression (the central exploit) + BDF rerun

**Files:**
- Test: `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/TransportTrustExploitTests.Integration.cs` (create; follow the existing integration-test harness in `Integration/ContributorTests.Integration.cs` for how the full pipeline / `DetectionPolicy.Default` is exercised)

- [ ] **Step 1: Write the failing integration test**

Using the existing integration harness pattern (build the orchestrator with real DI, run a synthesized request through detection):

```csharp
// Build a request from a PUBLIC direct peer (RemoteIpAddress = 203.0.113.9)
// carrying X-JA3-Hash set to a known-Chrome value, with TransportTrust.Mode = Auto.
// Assert: final verdict is NOT pushed toward human (no -0.15 TLS human bias applied);
//         transport.spoofed_edge_headers signal is present.
//
// Then build the SAME request from a LOOPBACK peer (127.0.0.1).
// Assert: the known-Chrome JA3 human bias IS applied (regression: trusted path unchanged).
```

Mirror the assertion style and harness setup from `Integration/ContributorTests.Integration.cs`. The two requests differ only by `Connection.RemoteIpAddress`.

- [ ] **Step 2: Run to verify it fails (before gate wired into full pipeline) or passes (if gate already effective)**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~TransportTrustExploit"`
Expected: PASS once Tasks 3-8 are in place and the service is registered (Task 4). If it fails, confirm `ITransportHeaderTrust` is resolved into the contributors by DI (check the registration order in Task 4 precedes contributor registration, and that the contributors' new optional parameter is being filled).

- [ ] **Step 3: Run the full suite for regressions**

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/`
Expected: PASS, no new failures. Pay attention to any existing TLS/H2/H3/TCP fingerprint tests that construct the contributors directly: they pass `transportTrust: null`, so they fall through to the legacy trust-all path and must be unaffected.

- [ ] **Step 4: Commit**

```bash
git add src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/TransportTrustExploitTests.Integration.cs
git commit -m "test(transport-trust): direct-peer JA3 spoof scores bot, loopback unchanged"
```

- [ ] **Step 5: BDF cloak rig rerun (manual verification, record result)**

After this plan lands together with G5 (always-loaded TLS corpus), re-run the BDF damru/Multilogin cloak scenarios and record whether the 0.07 score moves. This is the cross-check for memory note `project_bdf_cloak_scenarios_blocked`. Command (from the BDF rig docs):

Run: `dotnet test src/Mostlylucid.BotDetection.Orchestration.Tests/ --filter "FullyQualifiedName~BdfReplay"`
Expected: damru/Multilogin scenarios no longer score 0.07 (record the new value in the gap-remediation plan).

---

## Self-review

**Spec coverage:**
- Service `ITransportHeaderTrust` with decision order (allowlist -> private -> topology) : Tasks 3, 5-8. ✓
- Config `TransportTrust` (Auto/Strict/Off, TrustedProxyIps, TrustDetectedTopology, TrustPrivatePeers) : Task 1. ✓
- Distrust => ignore headers + fall back to Kestrel + weak `transport.spoofed_edge_headers` only when headers present : Tasks 5-8 (gate prefix = ignore; Kestrel reads remain ungated; spoof contribution guarded by `gatedHeaderPresent`). ✓
- Fold spoofable `behind_proxy` into the gate : Task 6 step 5 note (H2); the TLS `tls.behind_proxy` write is driven by `X-Forwarded-Proto` which is intentionally left ungated (scheme, not fingerprint) — documented as out of scope, acceptable. ✓
- Signal keys : Task 2. ✓
- DI registration : Task 4. ✓
- YAML params : Task 9. ✓
- Narrative + docs + Off warning : Task 10. ✓
- Default-safety (Auto, loopback trusted) regression : Tasks 5-8 loopback tests + Task 11. ✓
- Signature arm : explicitly deferred in Scope note (not a gap; conscious YAGNI). ✓

**Placeholder scan:** No "TBD"/"handle edge cases". Two intentional "follow the existing harness pattern" pointers (Task 10 narrative dict shape, Task 11 integration harness) reference concrete existing files (`DetectionNarrativeBuilder.cs`, `ContributorTests.Integration.cs`) because the exact dictionary/harness shape must match in-repo conventions; the engineer reads those files to match style. The decision logic, options, signal keys, service, and gate snippets are all complete code.

**Type consistency:** `TransportTrustResult(bool Trusted, string Reason)`, `TransportTrustMode { Auto, Strict, Off }`, `TransportTrustOptions { Mode, TrustedProxyIps, TrustDetectedTopology, TrustPrivatePeers }`, `ITransportHeaderTrust.Evaluate`, concrete `.Decide(HttpContext)`, signal keys `TransportHeadersTrusted` / `TransportTrustReason` / `TransportSpoofedEdgeHeaders`, and the `transportTrust` constructor parameter are used identically across all tasks. ✓
