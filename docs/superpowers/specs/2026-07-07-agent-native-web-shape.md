# Shape: agent-native web alignment (RFC 9421 verify + capability tokens + agent markdown)

**Date:** 2026-07-07
**Origin:** Coverage sweep vs ModPageSpeed 2.0 (three experimental features they ship off-by-default: RFC 9421 signature verify, `Authorization: License` capability tokens, per-agent markdown serving + synthesized `/llms.txt`).
**Purpose:** Architecture shape only. Per-feature specs will be written by dedicated agents against the boundaries below.
**Aligns with:** `reference_stylobot_intended_architecture` (ephemeral → StyloFlow → sfpkg pyramid); `feedback_centroids_not_rules`, `feedback_no_stylobot_bypasses_for_detection_issues`, `feedback_upstream_owns_no_stylobot_state`, `feedback_all_settings_configurable`, `feedback_licensing_gate`.

## Where the three features sit in the pyramid

| Feature | Ephemeral layer | StyloFlow layer | Pack layer |
|---|---|---|---|
| **RFC 9421 verify** (Web Bot Auth) | New `WebBotAuthAtom` (Verifier taxonomy) writes `identity.verified_bot_signed:true` + `identity.verified_bot_name:<name>` + `identity.verified_bot_key_id`. `PublicKeyRegistryAtom` (Coordinator taxonomy) holds known-agent keys, refreshed on a ScheduleCoordinator tick. | Manifest at `Manifests/detectors/webbotauth.detector.yaml` declares emits and reads `well_known_keys.refresh_hours` from `Options`. | Ships in FOSS `Mostlylucid.BotDetection` — verification is a first-class detection capability, not a paid-only feature. Same tier as `VerifiedBotAtom`. |
| **Capability tokens** (`Authorization: License`) | New `CapabilityTokenAtom` (Verifier taxonomy — **commercial**) parses the `Authorization: License` header, calls the FOSS generic `ITokenVerifier` (`TokenKind.SignedBearerToken`) with StyloFlow claim-name knobs, writes `auth.capability_token_valid:bool` + `auth.capability_claims:<serialized>`. FOSS owns the generic signed-token verifier only; the license *meaning* (scheme name, tier claims, vendor trust root) is commercial. | Manifest is a commercial manifest declaring emits and reading issuer trust-anchor list from `Options`. Existing `IActionPolicy` dispatches on token verdict → 401 / 402 / passthrough. | **Commercial.** FOSS ships only the licensing-agnostic verifier primitive. Per operator directive: FOSS carries zero licensing knowledge. |
| **Agent markdown serving** | No new atom needed on the read side — reuses `identity.verified_bot_signed` + `identity.verified_bot_name`. Response side needs a new `ResponseTransformCoordinator` (single instance, dispatches to per-content-type transformers). | New manifest kind: `Manifests/response-transforms/*.yaml`. Declares "when signal X is present, apply transformer Y to content-type Z". Loaded by the coordinator at boot. | Ships as its own sfpkg-distributable: **`Mostlylucid.BotDetection.AgentContent`**. Contains the HTML→Markdown transformer, the sitemap-driven `/llms.txt` synthesizer, and the manifest schema. Optional pack; not part of the default gateway image. |

## Shared primitives the three features need

The features share three new primitives. Build these once, in this order — every per-feature agent depends on them.

1. **`PublicKeyRegistry` service** (FOSS core).
   - Interface: `IPublicKeyRegistry.TryResolve(keyId) -> (name, publicKey)`.
   - Backed by an `EscalatorAtom` for durability + a `Coordinator` for scheduled refresh.
   - Source of truth is a JSON manifest fetched from a configured URL (Cloudflare publishes the AI-agent registry; anyone can host their own). Refresh cadence on `ScheduleCoordinator` `Tick1h`.
   - Same shape as `ThreatIntelStore` — well-known distribution point, durable snapshot, ephemeral in-memory index.
   - **This is the foundation for RFC 9421 verify. It ships first.**

2. **`ITokenVerifier` abstraction** (FOSS core, **licensing-agnostic**).
   - Interface: `ITokenVerifier.Verify(token, trustAnchors) -> TokenVerdict`.
   - Two implementations at first: RFC 9421 HTTP Message Signatures, and a generic **signed bearer token** (canonical-JSON + Ed25519 signature). Both are `sig + claims + expiry` shapes — one abstraction over both is cheap and prevents parallel crypto pipelines. **The "signed bearer token" impl has no knowledge that a consumer is verifying a license.** The commercial CapabilityTokenAtom points the configurable claim-name knobs at StyloFlow's convention; FOSS itself never mentions "license".
   - Uses only `System.Security.Cryptography` + NSec primitives. No third-party JOSE library, no dependency on `Mostlylucid.StyloFlow.Licensing`.
   - **Feeds WebBotAuthAtom (FOSS) directly, and the commercial CapabilityTokenAtom via configuration.**

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
| **WebBotAuthApprovalAtom agent** (foss) | Single atom `WebBotAuthApprovalAtom` (Verifier + Coordinator, singleton). Reads WBA headers directly, verify-once per (session, keyid) against `SessionAggregate.WebBotAuthVerdict`, emits beacon `webbotauth.presented` + resolved `identity.verified_bot_*` C3 outputs. Extends `SessionAggregate` with a `WebBotAuthCachedVerdict` slot. Integration into `identity.verified_bot_name` consumer chain. | Public-key-registry + Token-verifier + `SessionStore`. | FOSS |
| **CapabilityTokenAtom agent** | `CapabilityTokenAtom` (parses `Authorization: License`, calls FOSS `ITokenVerifier` with StyloFlow claim-name knobs), its commercial manifest, action-policy dispatch on `auth.capability_token_valid`, `RequiresCapabilityRuleExtension : IEndpointPolicyRuleExtension` (commercial matcher). | FOSS Token-verifier + FOSS `IEndpointPolicyRuleExtension` seam (C4a — wba-foundation adds). | **Commercial** |
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

### C2. `ITokenVerifier` + `TokenVerdict` (Token-verifier agent owns) — GENERIC, ZERO LICENSING KNOWLEDGE

```csharp
namespace Mostlylucid.BotDetection.Auth;

public interface ITokenVerifier
{
    TokenVerdict Verify(TokenInput input);
}

public readonly record struct TokenInput(
    TokenKind Kind,               // Rfc9421HttpSignature | SignedBearerToken
    string RawValue,              // full signature-input+signature block for 9421; base64(canonical-JSON) for signed bearer
    IReadOnlyDictionary<string, string> CoveredHeaders,   // request headers covered by the signature (9421 only)
    string RequestMethod,
    string RequestPath);

public sealed record TokenVerdict(
    TokenOutcome Outcome,         // Valid | InvalidSignature | Expired | UnknownKey | Malformed | MissingKey
    string? KeyId,                // populated when at least the header parsed
    string? SubjectName,          // AgentName for 9421 (from PublicKeyRegistry); value of SignedTokenSubjectClaim for bearer
    IReadOnlyDictionary<string, string>? Claims,   // raw claim map (bearer) OR the 9421 signature parameters
    TimeSpan Elapsed);            // verify-time — feeds the dashboard latency panel

public enum TokenKind    { Rfc9421HttpSignature, SignedBearerToken }
public enum TokenOutcome { Valid, InvalidSignature, Expired, UnknownKey, Malformed, MissingKey }
```

- One verifier, two `TokenKind`s. The 9421 impl resolves keys via `IPublicKeyRegistry`; the **SignedBearerToken** impl (internal name `SignedTokenVerifier`) reads a base64-encoded canonical-JSON blob + signature and verifies against `TokenVerifierOptions.TrustAnchors`. It surfaces the raw claim map only — it does NOT interpret "tier", "entitlement", "license", or "Authorization: License". Those concepts do not exist in FOSS.
- Signature/subject/expiry claim NAMES are configurable via `TokenVerifierOptions.SignedTokenSignatureField` (default `"signature"`), `SignedTokenSubjectClaim` (default `"sub"`), `SignedTokenExpiryClaim` (default `"exp"`). A commercial license consumer points these at StyloFlow's convention (`signature` / `issuedTo` / `expiry`) via configuration; FOSS ships the RFC-default names.
- Signature validation uses `Mostlylucid.BotDetection.Auth.ISignatureValidator` — a FOSS-side seam with a default `Ed25519SignatureValidator` implementation using NSec (already referenced by FOSS core). **FOSS has NO dependency on `Mostlylucid.StyloFlow.Licensing`** — that's the hard operator boundary this seam exists for. Commercial licensing binds its own `ISignatureValidator` implementation (or reuses the default) pointing at the vendor trust root.
- The default impl's canonical-content scheme (sorted keys, exclude the signature field, non-indented JSON) matches what `LicenseSigningService.GetSignableContent` in commercial produces — a byte-for-byte compat test lives in the **commercial** repo (`Stylobot.Commercial.LicenseGen.Tests`), not FOSS.
- Trust anchors (`TrustAnchor { base64PublicKey, label }`) live on `TokenVerifierOptions.TrustAnchors` — with the verifier, not the atom. The commercial `CapabilityTokenAtom` may hold policy config (LogOnly mode, claim→action maps) on its own commercial Options class, but never trust anchors.
- Verifier is a singleton, pure function of inputs — no per-request state, no IO. `Elapsed` measured internally so the caller can't fake it.

### C3. Signal keys the atoms write — ONE ATOM, beacon on sink, verdict on SessionAggregate

Sink keys are the API for downstream atoms and action policies. Locked here — any consumer reads via `sink.ReadHint(...)` / `sink.ReadBoolHint(...)`.

Verify is expensive (~21μs Ed25519, ~85μs ECDSA-P256 per foss's `00e4a053` bench — dominated by per-call key import). Doing it per-request is waste on session-stable identities. Solution: verify-once per session per keyid; cache the verdict on the canonical per-session state.

**Two operator-directed invariants shape this design:**

1. **Sink is presumed INSECURE (zero-PII).** Auth material — bearer tokens, signature blobs, base strings — MUST NOT land on the sink. Downstream sink readers (dashboard, logs, exports, other atoms) are broadcast targets; even public-in-transit RFC 9421 signature material shouldn't leak there. Sink carries only a non-sensitive BEACON and the RESOLVED output signals.
2. **Atoms are stateless singletons.** `services.AddSingleton<IDetectorAtom, X>()` — one instance serves all sessions concurrently. Session-scoped state lives on `SessionStore.Aggregates` (`SessionStore.cs:376`, `ConcurrentDictionary<sessionId, SessionAggregate>`, session-lifetime, discarded at session-boundary). A private field on an atom or a `ConcurrentDictionary<sessionId, verdict>` on the atom IS the parasitic-store class the operator ruled out.

**One atom — `WebBotAuthApprovalAtom` (FOSS, foss agent owns) — Verifier + Coordinator taxonomy, singleton.**

Runs per-request. Reads `Signature` + `Signature-Input` HTTP headers directly (no cross-atom raw-material handoff). Looks up caller's `SessionAggregate.WebBotAuthVerdict` slot. On first sight per (session, keyid), calls `ITokenVerifier.Verify` once; caches (keyid, verdict, subject, algorithm) on the SessionAggregate. On subsequent same-session same-keyid requests, uses cached verdict — no re-verify. Session L1 identity (UA/IP HMAC session-stable per `reference_session_layer_and_fingerprint_levels`) is the outer trust window; keyid is the inner check-if-changed key.

```csharp
public static partial class SignalKeys
{
    // Beacon — carries NO auth material. Bool: "WBA headers were structurally present on this request."
    public const string WebBotAuthPresented = "webbotauth.presented";                // bool

    // Resolved output — emitted from cached OR fresh verdict, downstream consumers read these
    public const string VerifiedBotSigned    = "identity.verified_bot_signed";      // bool
    public const string VerifiedBotName      = "identity.verified_bot_name";        // string, e.g. "GPTBot"
    public const string VerifiedBotKeyId     = "identity.verified_bot_key_id";      // string — public identifier, not sensitive
    public const string VerifiedBotAlgorithm = "identity.verified_bot_algorithm";   // string
    public const string SignatureVerdict     = "identity.signature_verdict";        // TokenOutcome as string
}
```

**SessionAggregate extension (foss adds):**

```csharp
// on SessionAggregate — session-lifetime, discarded at session boundary
public WebBotAuthCachedVerdict? WebBotAuthVerdict { get; set; }

public sealed record WebBotAuthCachedVerdict(
    string KeyId,            // public identifier; NOT the signature or base string
    TokenOutcome Verdict,
    string? SubjectName,     // resolved bot name (e.g. "GPTBot")
    string? Algorithm);      // resolved alg (e.g. "ed25519")
```

- **NO raw signature or base string on SessionAggregate.** Session state should not hold auth-material blobs; verify is done once + resolved metadata cached. Storing raw signature bytes on session state that outlasts the request creates a compliance surface for no correctness benefit.
- **Check-if-changed key.** Current request's parsed `keyid` (from `Signature-Input` header) vs `SessionAggregate.WebBotAuthVerdict.KeyId`. Match = use cache; mismatch (or no cached verdict) = call verifier + update cache. Same session presenting a different keyid = re-verify.
- **Session-scope + expiry.** SessionAggregate lifecycle IS the TTL. `SessionStore` discards the aggregate at session boundary; the cached verdict goes with it. No explicit TTL knob.
- **Sink safety.** ONLY `webbotauth.presented` (bool beacon) + `identity.verified_bot_*` (resolved outputs) touch the sink. Never the raw signature, base string, or full `Signature-Input` header value. This is uniform for BOTH RFC 9421 (public-in-transit but defensive) and the commercial SignedBearerToken case (actually secret) — no per-token-kind judgment calls.
- **Not `IFingerprintApprovalStore`.** That store is durable / operator-granted. Session-ephemeral WBA approvals are a distinct lane — `SessionAggregate` slot, not a store insert.
- **No parasitic import cache.** Do NOT add a per-key import cache in `PublicKeyRegistry` or `CryptoSignatureValidator`. The verify-once-per-session amortization IS the perf answer.
- **Nudges centroids, doesn't clobber.** `IdentityArchetypeRegistry` seeds `verified-<botname>` centroid on the resolved output; no archetype short-circuit.
- **Session-trust posture.** Per operator directive: once verified within a session, later same-keyid requests trust the earlier verify. This trades one round of crypto against session-hijack-post-verify risk. L1 UA/IP HMAC session stability is the operating assumption that justifies it; if the operator later wants tighter semantics (reverify every N requests), that becomes an Options knob on `WebBotAuthOptions` — leave a hook.

**Commercial-owned (CapabilityTokenAtom agent — string values locked so downstream consumers are stable):**

```csharp
// Lives in commercial (e.g. Stylobot.Commercial.Licensing.CapabilityAtom.CapabilitySignalKeys).
// Values are frozen here to prevent drift.
public static class CapabilitySignalKeys
{
    public const string CapabilityTokenValid    = "auth.capability_token_valid";    // bool
    public const string CapabilityTokenSubject  = "auth.capability_token_subject";  // string
    public const string CapabilityClaims        = "auth.capability_claims";         // JSON string of claim map
    public const string CapabilityTokenVerdict  = "auth.capability_token_verdict";  // TokenOutcome as string
}
```

- Manifests declare these in `emits.on_complete` per the existing manifest schema — no new manifest shape. Commercial atoms publish their manifest under the commercial repo's manifest tree.

### C4. `EndpointPolicy` extensibility seam (FOSS) + `RequiresCapability` matcher (commercial)

Two-part change, licensing-agnostic FOSS seam + license-specific commercial matcher.

**C4a — FOSS-side seam (wba-foundation to add — grep confirmed no seam exists today; `ConfigEndpointPolicyResolver.Match()` is hard-coded).**

```csharp
namespace Mostlylucid.BotDetection.EndpointPolicy;

/// <summary>
/// External-package hook for contributing named policy-rule matchers.
/// FOSS knows nothing about the matcher's semantics — only its name and pass/fail vote.
/// </summary>
public interface IEndpointPolicyRuleExtension
{
    /// <summary>YAML key this extension binds to (e.g. "requiresCapability").</summary>
    string RuleName { get; }

    /// <summary>Evaluate the rule payload for this request. Return true = rule satisfied.</summary>
    bool Matches(EndpointPolicyExtensionContext context);
}

public readonly record struct EndpointPolicyExtensionContext(
    HttpContext HttpContext,
    SignalSink Sink,                            // extension reads signals written earlier in the pipeline
    IReadOnlyDictionary<string, object?> RulePayload);  // the YAML sub-tree under RuleName
```

- `EndpointPolicyRule` gains an `Extensions: Dictionary<string, object?>` field. YAML keys not baked into the compiled matchers land here.
- `ConfigEndpointPolicyResolver.Match()` after all baked-in checks, iterates registered `IEndpointPolicyRuleExtension` instances; for each `RuleName` present in `rule.Extensions`, calls `Matches`. A `false` from any extension = rule does not match this rule row (fall through to the next rule).
- FOSS ships zero extensions itself. The seam is licensing-agnostic.

**C4b — commercial `RequiresCapability` matcher.**

```yaml
# Commercial config, additive YAML under an existing EndpointPolicyRule:
- path: "/api/premium/**"
  requiresCapability:                   # commercial extension key
    claim: "stylobot.tier"
    value: "enterprise"                 # any of Value / OneOf / MinTier
    onMissing: 401                      # 401 | 402 | passthrough (per modpagespeed's 401/402/pass semantic)
    onInvalid: 402
```

- Commercial package registers `RequiresCapabilityRuleExtension : IEndpointPolicyRuleExtension` (RuleName = `"requiresCapability"`). It reads the parsed capability from the sink (written by the commercial `CapabilityTokenAtom` earlier in the pipeline) and votes match/no-match. On no-match, the atom's action-policy dispatch takes care of the 401/402 status (matcher just gates the rule row).
- Coordinate the seam design with the feature/foss agent — the health-endpoint `Source` matcher discussion (see `feature-review-health-endpoint-design.md`) may land as a baked-in `Source` field OR as the first user of this same extension seam. Prefer baked-in for `Source` (universal concept), use the extension seam for anything license-flavored.

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
Mostlylucid.BotDetection.Auth.TokenVerifierOptions               // TrustAnchors + SignedTokenSignatureField/SubjectClaim/ExpiryClaim knobs

// Commercial (owned by CapabilityTokenAtom agent — configures the FOSS verifier for StyloFlow claim names)
Stylobot.Commercial.Licensing.CapabilityAtom.CapabilityTokenOptions

// AgentContent pack
Mostlylucid.BotDetection.AgentContent.ResponseTransformOptions
Mostlylucid.BotDetection.AgentContent.LlmsTxtSynthesizerOptions
```

Every knob per `feedback_all_settings_configurable` lives on one of these. No `IConfiguration.GetValue(...)` outside the Options binder.

### C8. Ephemeral taxonomy assignments (locked)

Per `reference_stylobot_intended_architecture`, every new type gets a taxonomy label. Locked here so nobody accidentally builds a Sensor where a Verifier belongs.

| New type | Taxonomy | Ships in |
|---|---|---|
| `PublicKeyRegistry` service | Coordinator | FOSS |
| `PublicKeyRegistryAtom` (durability wrapper) | Escalator | FOSS |
| `WebBotAuthApprovalAtom` (single atom — reads headers, verify-once per session, cache on SessionAggregate) | Verifier + Coordinator | FOSS |
| `CapabilityTokenAtom` | Verifier | **Commercial** |
| `ITokenVerifier` implementations (`Rfc9421Verifier`, `SignedTokenVerifier`) | Molecule (stateless, pure) | FOSS |
| `IEndpointPolicyRuleExtension` seam | Extension seam | FOSS |
| `RequiresCapabilityRuleExtension` | Guard | **Commercial** |
| `IResponseTransformer` implementations | Molecule | AgentContent pack |
| `ResponseTransformCoordinator` middleware | Coordinator | AgentContent pack |

## What this shape deliberately does NOT specify

Test file layout, error-message wording, dashboard chart shapes, per-transformer performance budgets, and exact refresh cadences (initial defaults suggested above, tunable via Options). Each per-feature spec proposes those and I'll review only against the contracts above.

## Related

- Existing FOSS AI-scraper detection at `src/Mostlylucid.BotDetection/Orchestration/Atoms/AiScraperAtom.cs:32,40` already probes for the discovery endpoints and `/llms.txt`. That atom stays as-is; the new features add verification on top.
- Existing licensing infrastructure at `src/Stylobot.Commercial.Licensing/` gives us the token-verify primitive to reuse. `DomainEntitlementValidator` is the working example.
- Existing threat-intel refresh pattern (`ThreatIntelStore` + `FeedPollingService`) is the template for the public-key registry refresh.
- Existing per-BotType action policy dispatch (`ActionPolicyRegistry` + `EscalateToLearningActionPolicy` et al.) is the seam where WebBotAuth verdicts and CapabilityToken verdicts hook in.
- `docs/architecture/signal-assay.md` (referenced in `project_signal_assay` memory) — same environment-adaptation family; applies here for the "public-key registry might be unreachable" absent-signal case.
