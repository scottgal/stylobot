# Shape: agent-native web alignment (RFC 9421 verify + capability tokens + agent markdown)

**Date:** 2026-07-07
**Origin:** Coverage sweep vs ModPageSpeed 2.0 (three experimental features they ship off-by-default: RFC 9421 signature verify, `Authorization: License` capability tokens, per-agent markdown serving + synthesized `/llms.txt`).
**Purpose:** Architecture shape only. Per-feature specs will be written by dedicated agents against the boundaries below.
**Aligns with:** `reference_stylobot_intended_architecture` (ephemeral → StyloFlow → sfpkg pyramid); `feedback_centroids_not_rules`, `feedback_no_stylobot_bypasses_for_detection_issues`, `feedback_upstream_owns_no_stylobot_state`, `feedback_all_settings_configurable`, `feedback_licensing_gate`.

## Where the three features sit in the pyramid

| Feature | Ephemeral layer | StyloFlow layer | Pack layer |
|---|---|---|---|
| **RFC 9421 verify** (Web Bot Auth) | New `WebBotAuthAtom` (Verifier taxonomy) writes `identity.verified_bot_signed:true` + `identity.verified_bot_name:<name>` + `identity.verified_bot_key_id`. `PublicKeyRegistryAtom` (Coordinator taxonomy) holds known-agent keys, refreshed on a ScheduleCoordinator tick. | Manifest at `Manifests/detectors/webbotauth.detector.yaml` declares emits and reads `well_known_keys.refresh_hours` from `Options`. | Ships in FOSS `Mostlylucid.BotDetection` — verification is a first-class detection capability, not a paid-only feature. Same tier as `VerifiedBotAtom`. |
| **Capability tokens** (`Authorization: License`) | New `CapabilityTokenAtom` (Verifier taxonomy) parses the header, verifies signature via existing `StyloFlow.Licensing.ISignatureValidator`, writes `auth.capability_token_valid:bool` + `auth.capability_claims:<serialized>`. | Manifest declares emits and reads issuer trust-anchor list from `Options`. Existing `IActionPolicy` dispatches on token verdict → 401 / 402 / passthrough. | Ships in FOSS. Verifier is a primitive; policy on top of it (allow/require/etc.) is per-endpoint via `EndpointPolicy` and per-domain via the commercial `Domains.Ui` pack. |
| **Agent markdown serving** | No new atom needed on the read side — reuses `identity.verified_bot_signed` + `identity.verified_bot_name`. Response side needs a new `ResponseTransformCoordinator` (single instance, dispatches to per-content-type transformers). | New manifest kind: `Manifests/response-transforms/*.yaml`. Declares "when signal X is present, apply transformer Y to content-type Z". Loaded by the coordinator at boot. | Ships as its own sfpkg-distributable: **`Mostlylucid.BotDetection.AgentContent`**. Contains the HTML→Markdown transformer, the sitemap-driven `/llms.txt` synthesizer, and the manifest schema. Optional pack; not part of the default gateway image. |

## Shared primitives the three features need

The features share three new primitives. Build these once, in this order — every per-feature agent depends on them.

1. **`PublicKeyRegistry` service** (FOSS core).
   - Interface: `IPublicKeyRegistry.TryResolve(keyId) -> (name, publicKey)`.
   - Backed by an `EscalatorAtom` for durability + a `Coordinator` for scheduled refresh.
   - Source of truth is a JSON manifest fetched from a configured URL (Cloudflare publishes the AI-agent registry; anyone can host their own). Refresh cadence on `ScheduleCoordinator` `Tick1h`.
   - Same shape as `ThreatIntelStore` — well-known distribution point, durable snapshot, ephemeral in-memory index.
   - **This is the foundation for RFC 9421 verify. It ships first.**

2. **`ITokenVerifier` abstraction** (FOSS core).
   - Interface: `ITokenVerifier.Verify(token, issuerTrustAnchors) -> TokenVerdict`.
   - Two implementations at first: RFC 9421 HTTP Message Signatures, and StyloFlow license capability tokens. Both are `sig + claims + expiry` shapes — one abstraction over both is cheap and prevents parallel crypto pipelines.
   - Uses only `System.Security.Cryptography` primitives. No third-party JOSE library — we've already argued this elsewhere.
   - **Feeds both WebBotAuthAtom and CapabilityTokenAtom.**

3. **`ResponseTransformCoordinator`** (new pack, opt-in).
   - Middleware that runs POST-detection, PRE-response-body-write.
   - Reads sink signals (verified bot identity), reads content-type, dispatches to registered `IResponseTransformer` (Molecule taxonomy — pure function of input body + signals → output body).
   - Loads its transformer rules from `response-transforms/*.yaml` manifests via the same YAML loader shape as `detectors/*.yaml`.
   - **This is a new middleware seam. Design carefully — it's the first response-body-mutation in the gateway. See "not modifying response bodies" boundary below.**

## Alignment guardrails (do not violate)

- **No path-based bypass** (`feedback_no_stylobot_bypasses_for_detection_issues`). Verified-bot allow decisions come from the signal + policy pipeline, never a hardcoded path exempt. `/llms.txt` traffic still goes through detection; the response transformer just serves different content once the verdict is in.
- **No caches unless asked** (`feedback_no_caches_freshness_over_locality`). Public-key registry has a bounded TTL + explicit refresh cadence. Capability tokens are verified on every request (crypto is cheap). Markdown transforms compute from upstream response on the wire — no output cache.
- **Upstream owns content** (`feedback_upstream_owns_no_stylobot_state`). The markdown transformer reads the upstream HTML response and produces markdown; it does NOT fetch the sitemap independently or persist a shadow content store. `/llms.txt` synthesis reads the upstream `sitemap.xml` on demand + a short TTL (30 min default).
- **Provenance preservation.** Response transform pack MUST NOT re-encode images. Static-asset paths are pass-through; only HTML→Markdown is a body mutation. Called out because modpagespeed's ancestor (and its optimize-image filter) is the reason C2PA is dead across the web today; the memory `feedback_no_caches_freshness_over_locality` sub-rule "we don't touch image bodies" becomes an explicit invariant of the AgentContent pack.
- **Centroids over rules** (`feedback_centroids_not_rules`). RFC 9421 verify produces a verdict, not a rule-set match. The verdict feeds into the identity archetype system alongside existing evidence — the archetype registry stays the classification substrate. `identity.verified_bot_signed:true` seeds the corresponding `verified-<botname>` centroid; it doesn't short-circuit archetype matching.
- **Licensing** (`feedback_licensing_gate`). All three features ship in FOSS as capabilities. The paid gate is on the *scale* and *operational tooling* around them (multi-domain registry management, capability-token issuer UI, response-transform manifest editor). Detection-and-serve primitives are FOSS; management surface is commercial. This is the same shape as `EndpointPolicy` and `IDashboardEventStore`.
- **All settings on Options** (`feedback_all_settings_configurable`). No magic numbers. Every threshold, refresh interval, trust anchor, transformer output size cap lives on an Options class bound from `BotDetection:*` configuration.

## Per-agent boundaries (for the agents you'll spin)

Each agent gets its own spec to write. To parallelize without collision:

| Agent | Owns | Depends on | Ships in |
|---|---|---|---|
| **Public-key-registry agent** | `PublicKeyRegistry`, `PublicKeyRegistryAtom`, the JSON manifest schema, the scheduled-refresh coordinator, dashboard visibility for the current key set + last refresh. | Nothing prior. Foundation. | FOSS |
| **Token-verifier agent** | `ITokenVerifier`, the RFC 9421 impl, the license-capability-token impl, unit tests, error taxonomy. | Public-key-registry (for RFC 9421 key resolution). | FOSS |
| **WebBotAuthAtom agent** | `WebBotAuthAtom` (Verifier taxonomy), its manifest, verdict-honest override plumbing so a valid signature promotes to `BotType.VerifiedBot` with a named identity, integration into `identity.verified_bot_name` consumer chain. | Public-key-registry + Token-verifier. | FOSS |
| **CapabilityTokenAtom agent** | `CapabilityTokenAtom`, its manifest, action-policy dispatch on `auth.capability_token_valid`, per-EndpointPolicy `RequiresCapability:<claim>` rule. | Token-verifier. | FOSS |
| **AgentContent pack agent** | The `Mostlylucid.BotDetection.AgentContent` pack: `ResponseTransformCoordinator`, `IResponseTransformer`, HTML→Markdown transformer, `/llms.txt` synthesizer from sitemap, `response-transforms/*.yaml` manifest schema. | Nothing on the ephemeral side (reads existing sink signals). Depends on WebBotAuthAtom producing `identity.verified_bot_signed`. | New sfpkg |
| **Commercial-UI agent** (feature agent, existing) | Dashboard surfaces: registered public keys view, capability token issuer + claim editor, per-response-transform rule editor. | All four above. Ships as commercial dashboard packs. | Commercial |

Order to build: `PublicKeyRegistry` → `ITokenVerifier` → `WebBotAuthAtom` and `CapabilityTokenAtom` in parallel → `AgentContent` pack → commercial UI packs. First three are on the FOSS critical path; last two can start once atoms land.

## What ships in staging first

The chain `PublicKeyRegistry` → `Token-verifier` → `WebBotAuthAtom` is the smallest slice that produces user-visible value (verified AI-agent traffic labeled correctly in the dashboard, no policy change yet). Ship that chain to staging with the feature default-off. Turn observe-only on for a week to see how the Cloudflare AI-agent registry looks against real traffic; only then wire it to action policies. Matches the observe-only-by-default pattern modpagespeed uses.

## Rollout gates + kill switches

- Each atom has a `BotDetection:*:Enabled` flag defaulting to false in FOSS `appsettings.json`.
- The AgentContent pack registration is opt-in (pack is not referenced by the default gateway image; a host that wants it references the package).
- Response-transform rules are per-transformer + per-content-type flagged; deploy a rule with `Mode: ObserveOnly` before flipping to `Mode: Transform`.
- Capability-token action policies default to `Mode: LogOnly` (401/402 responses computed but not sent) until an operator opts in.

## Contracts — LOCKED. Per-feature agents build against these; do not re-invent.

The interfaces, signal keys, manifest field names, and Options class names below are fixed by this shape doc. Per-feature specs bind them; per-feature commits implement them. Departures need a follow-up shape-doc edit here, not a per-feature spec deviation.

### C1. `IPublicKeyRegistry` (Public-key-registry agent owns)

```csharp
namespace Mostlylucid.BotDetection.WebBotAuth;

public interface IPublicKeyRegistry
{
    /// <summary>Non-blocking lookup by kid (JOSE-style key id).</summary>
    bool TryResolve(string keyId, out PublicKeyEntry entry);

    /// <summary>Snapshot of the whole registry — dashboard visibility.</summary>
    IReadOnlyList<PublicKeyEntry> Snapshot();

    /// <summary>Timestamp of the most recent successful refresh (or null if never).</summary>
    DateTimeOffset? LastRefreshedUtc { get; }
}

public sealed record PublicKeyEntry(
    string KeyId,
    string AgentName,            // e.g. "GPTBot", "PerplexityBot"
    ReadOnlyMemory<byte> PublicKey,
    string Algorithm,             // "ed25519", "ecdsa-p256-sha256", etc. — matches RFC 9421 alg names
    DateTimeOffset? NotAfter,     // key rotation
    string Source);               // manifest URL the key came from
```

- Refresh is coordinator-driven, not per-request. Never blocks a request thread on IO.
- `Snapshot()` returns a stable list — no locking on the caller side. Coordinator swaps under a lock, readers see immutable snapshots.

### C2. `ITokenVerifier` + `TokenVerdict` (Token-verifier agent owns)

```csharp
namespace Mostlylucid.BotDetection.Auth;

public interface ITokenVerifier
{
    TokenVerdict Verify(TokenInput input);
}

public readonly record struct TokenInput(
    TokenKind Kind,               // Rfc9421HttpSignature | LicenseCapability
    string RawValue,              // full signature-input+signature block for 9421; base64 blob for capability
    IReadOnlyDictionary<string, string> CoveredHeaders,   // request headers covered by the signature (9421 only)
    string RequestMethod,
    string RequestPath);

public sealed record TokenVerdict(
    TokenOutcome Outcome,         // Valid | InvalidSignature | Expired | UnknownKey | Malformed | MissingKey
    string? KeyId,                // populated when at least the header parsed
    string? SubjectName,          // AgentName for 9421 (from PublicKeyRegistry); IssuedTo for capability
    IReadOnlyDictionary<string, string>? Claims,   // capability claims OR the 9421 signature parameters
    TimeSpan Elapsed);            // verify-time — feeds the dashboard latency panel

public enum TokenKind    { Rfc9421HttpSignature, LicenseCapability }
public enum TokenOutcome { Valid, InvalidSignature, Expired, UnknownKey, Malformed, MissingKey }
```

- One verifier, two `TokenKind`s. The 9421 impl resolves keys via `IPublicKeyRegistry`; the capability impl resolves via `Mostlylucid.BotDetection.Auth.ISignatureValidator` — a FOSS-side seam with a default `Ed25519SignatureValidator` implementation using NSec (already referenced by FOSS core). Do NOT add a FOSS dependency on `StyloFlow.Licensing`; commercial licensing can supply its own `ISignatureValidator` binding to the vendor trust root later, same interface. The default impl replicates `LicenseSigningService.GetSignableContent`'s canonical-content scheme (sorted keys, exclude "signature", non-indented) so tokens minted by the existing licensing pipeline verify byte-for-byte.
- Capability trust anchors live on `TokenVerifierOptions.CapabilityTrustAnchors` (list of `{ base64PublicKey, label }`) — with the verifier, not the atom. `CapabilityTokenOptions` (owned by the caps-atom agent) holds policy config (LogOnly mode, claim→action maps) but never trust anchors.
- Verifier is a singleton, pure function of inputs — no per-request state, no IO. `Elapsed` measured internally so the caller can't fake it.

### C3. Signal keys the atoms write (WebBotAuthAtom + CapabilityTokenAtom agents own)

Sink keys are the API for downstream atoms and action policies. Locked here — any consumer reads via `sink.ReadHint(...)` / `sink.ReadBoolHint(...)`.

```csharp
namespace Mostlylucid.BotDetection.Models;

public static partial class SignalKeys
{
    // WebBotAuthAtom (RFC 9421)
    public const string VerifiedBotSigned    = "identity.verified_bot_signed";      // bool
    public const string VerifiedBotName      = "identity.verified_bot_name";        // string, e.g. "GPTBot"
    public const string VerifiedBotKeyId     = "identity.verified_bot_key_id";      // string
    public const string VerifiedBotAlgorithm = "identity.verified_bot_algorithm";   // string
    public const string SignatureVerdict     = "identity.signature_verdict";        // TokenOutcome as string

    // CapabilityTokenAtom
    public const string CapabilityTokenValid    = "auth.capability_token_valid";    // bool
    public const string CapabilityTokenSubject  = "auth.capability_token_subject";  // string
    public const string CapabilityClaims        = "auth.capability_claims";         // JSON string of claim map
    public const string CapabilityTokenVerdict  = "auth.capability_token_verdict";  // TokenOutcome as string
}
```

- Manifests declare these in `emits.on_complete` per the existing manifest schema — no new manifest shape.
- Seeds the `verified-<botname>` centroid via `IdentityArchetypeRegistry` — WebBotAuthAtom nudges but does not clobber archetype match.

### C4. `EndpointPolicy` extensions (CapabilityTokenAtom agent owns the rule matcher)

```yaml
# Manifests/detectors/... unchanged.
# New EndpointPolicy rule shape (additive):
- path: "/api/premium/**"
  requiresCapability:                   # NEW field, optional
    claim: "stylobot.tier"
    value: "enterprise"                 # any of Value / OneOf / MinTier
  onMissing: 401                        # 401 | 402 | passthrough (per modpagespeed's 401/402/pass semantic)
  onInvalid: 402
```

- Same `EndpointPolicyResolver` seam feature agent is extending for the health-endpoint `Source` matcher — one composition point, two additive rule kinds. Coordinate with feature agent so both land against the same resolver.

### C5. `IResponseTransformer` (AgentContent pack agent owns)

```csharp
namespace Mostlylucid.BotDetection.AgentContent;

public interface IResponseTransformer
{
    /// <summary>Content-Type the transformer accepts on input (e.g. "text/html").</summary>
    string InputContentType { get; }

    /// <summary>Content-Type the transformer produces (e.g. "text/markdown").</summary>
    string OutputContentType { get; }

    /// <summary>True if this transformer should run given the current sink state + upstream response headers.</summary>
    bool ShouldTransform(SignalSink sink, IReadOnlyDictionary<string, string> upstreamHeaders);

    /// <summary>Pure function: input body → output body. Reads no state; writes no state.</summary>
    Task<TransformResult> TransformAsync(ReadOnlyMemory<byte> upstreamBody, CancellationToken ct);
}

public sealed record TransformResult(
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> AdditionalHeaders);   // e.g. Vary, Content-Language additions
```

- Molecule taxonomy per the ephemeral pyramid — stateless, pure.
- Transformers register via `services.AddResponseTransformer<T>()`. No YAML manifest wiring; the manifest describes which transformer applies to which path/signal combination, not what the transformer does.

### C6. Response-transform manifest shape (AgentContent pack agent owns)

```yaml
# Manifests/response-transforms/agent-markdown.yaml
name: AgentMarkdownForVerifiedBots
priority: 50
enabled: true
description: Serve markdown-rendered HTML to Web-Bot-Auth-verified AI agents

matches:
  requiredSignals:
    - key: identity.verified_bot_signed
      value: true
  optionalSignals: []
  inputContentType: text/html
  requestPathGlob: "**/*"                 # per-URL applicability filter

transform:
  transformer: HtmlToMarkdownTransformer  # must match a registered IResponseTransformer

mode: ObserveOnly                          # ObserveOnly | Transform
sampleRate: 1.0                            # 0..1 — observe-only counts, transform actually applies

metrics:
  emit:
    - response.transform.observe_count
    - response.transform.applied_count
```

### C7. Options class names (all agents own their own; names locked)

```csharp
// FOSS
Mostlylucid.BotDetection.WebBotAuth.PublicKeyRegistryOptions
Mostlylucid.BotDetection.WebBotAuth.WebBotAuthOptions
Mostlylucid.BotDetection.Auth.TokenVerifierOptions
Mostlylucid.BotDetection.Auth.CapabilityTokenOptions

// AgentContent pack
Mostlylucid.BotDetection.AgentContent.ResponseTransformOptions
Mostlylucid.BotDetection.AgentContent.LlmsTxtSynthesizerOptions
```

Every knob per `feedback_all_settings_configurable` lives on one of these. No `IConfiguration.GetValue(...)` outside the Options binder.

### C8. Ephemeral taxonomy assignments (locked)

Per `reference_stylobot_intended_architecture`, every new type gets a taxonomy label. Locked here so nobody accidentally builds a Sensor where a Verifier belongs.

| New type | Taxonomy |
|---|---|
| `PublicKeyRegistry` service | Coordinator |
| `PublicKeyRegistryAtom` (durability wrapper) | Escalator |
| `WebBotAuthAtom` | Verifier |
| `CapabilityTokenAtom` | Verifier |
| `ITokenVerifier` implementations | Molecule (stateless, pure) |
| `IResponseTransformer` implementations | Molecule |
| `ResponseTransformCoordinator` middleware | Coordinator |

## What this shape deliberately does NOT specify

Test file layout, error-message wording, dashboard chart shapes, per-transformer performance budgets, and exact refresh cadences (initial defaults suggested above, tunable via Options). Each per-feature spec proposes those and I'll review only against the contracts above.

## Related

- Existing FOSS AI-scraper detection at `src/Mostlylucid.BotDetection/Orchestration/Atoms/AiScraperAtom.cs:32,40` already probes for the discovery endpoints and `/llms.txt`. That atom stays as-is; the new features add verification on top.
- Existing licensing infrastructure at `src/Stylobot.Commercial.Licensing/` gives us the token-verify primitive to reuse. `DomainEntitlementValidator` is the working example.
- Existing threat-intel refresh pattern (`ThreatIntelStore` + `FeedPollingService`) is the template for the public-key registry refresh.
- Existing per-BotType action policy dispatch (`ActionPolicyRegistry` + `EscalateToLearningActionPolicy` et al.) is the seam where WebBotAuth verdicts and CapabilityToken verdicts hook in.
- `docs/architecture/signal-assay.md` (referenced in `project_signal_assay` memory) — same environment-adaptation family; applies here for the "public-key registry might be unreachable" absent-signal case.
