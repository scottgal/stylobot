# Stylobot Runtime Security Review

Date: 2026-04-19

Status update: the main runtime hardening items from this review have now been applied in-tree.

Resolved in the current tree:

- client-facing `X-Bot-*` verdict leakage removed from proxied responses
- upstream CSP / frame protections no longer stripped by the CLI
- challenge bypass tokens are bound to request signature + host context
- browser demo callback is now demo-only, token-gated, size-limited, and isolated from the learning bus
- the CLI now defaults to `production`
- `/health` now returns minimal JSON only
- `/metrics` is loopback-only by default unless explicitly opened
- runtime bot-list and ONNX downloads are opt-in instead of default-on
- browser version scraping now stays offline when remote version feeds are disabled

## Scope

This review covers Stylobot as it runs today in this repository:

- the CLI reverse-proxy host in `Mostlylucid.BotDetection.Console`
- the request pipeline and action policies in `Mostlylucid.BotDetection`
- background learning, enrichment, feed/model downloads, and optional LLM egress

It is intentionally runtime-focused. The question is not "is the detector logic good?" but "what attack surface exists, what can an attacker abuse, and what should be hardened before exposure to the public internet?"

## External Surfaces

The current host exposes or can expose these surfaces:

- `/{proxied app}` via YARP
- `/health`
- `/metrics` (loopback-only by default)
- `/test-client-side.html` in `demo` / `learning` mode only
- `/api/bot-detection/test-status` in `demo` / `learning` mode only
- `/api/bot-detection/client-result` in `demo` / `learning` mode only
- `/stylobot-learning/{**path}` in `demo` and `learning` modes
- `/bot-detection/challenge/verify` when challenge flows are enabled in consumers of the library

The runtime also performs outbound calls when configured:

- bot/user-agent feed downloads
- ONNX model download from HuggingFace
- optional cloud LLM classification
- optional Project Honeypot DNS lookups

## Findings At Review Time

### 1. High: production proxying strips upstream browser security headers and leaks defense telemetry to the client

Evidence:

- `Mostlylucid.BotDetection.Console/Transforms/BotDetectionResponseTransform.cs:29-42`
- `Mostlylucid.BotDetection.Console/Transforms/BotDetectionResponseTransform.cs:45-62`

What happens:

- every proxied response gets `X-Bot-Detection-Callback-Url`
- every detected request gets client-visible `X-Bot-Detection`, `X-Bot-Probability`, and sometimes `X-Bot-Name`
- in non-demo mode, upstream `Content-Security-Policy`, `Content-Security-Policy-Report-Only`, and `X-Frame-Options` are removed

Why this matters:

- stripping CSP and frame protections weakens the protected application, not just Stylobot
- the client-visible detection headers give adversaries an oracle for tuning evasion
- a hostile client can iterate on headers, timing, and path shape and watch the score change in real time

Recommended mitigation:

- never remove upstream CSP or frame protections globally
- if client-side collection needs script execution, inject a narrowly-scoped nonce/hash-based allowance instead of deleting the policy
- make all client-visible `X-Bot-*` headers opt-in and default-off in production
- keep gateway-to-upstream detection headers separate from client-facing headers

### 2. High: challenge completion tokens are bearer tokens, not solver-bound tokens

Evidence:

- `Mostlylucid.BotDetection/Actions/ChallengeActionPolicy.cs:123-171`

What happens:

- the challenge token only contains `expiry + HMAC(expiry)`
- token validation checks expiry and HMAC only
- the token is not bound to IP, signature, UA, approval record, or challenge id

Why this matters:

- one successful solve can be replayed across clients until expiry
- bot operators can solve once, distribute the cookie, and bypass later challenges
- this weakens the proof-of-work flow from "prove this client paid the cost" into "present any valid recent token"

Recommended mitigation:

- bind the token to at least `signature + expiry + action policy + optional host`
- ideally bind it to the consumed challenge record id and solver signature
- shorten default lifetime for challenge bypass tokens
- in multi-instance deployments, use a shared secret and shared validation context

### 3. High: the public client-result callback is a learning-poisoning and queue-starvation surface

Evidence:

- `Mostlylucid.BotDetection.Console/Program.cs:682-777`

What happens:

- the endpoint is public
- it reads the full body into memory with `ReadToEndAsync()`
- it trusts `serverDetection` from the client payload instead of server-side state
- it publishes `ClientSideValidation` events onto the shared learning bus

Why this matters:

- an attacker can submit arbitrary "server said bot/human" labels and arbitrary client capability data
- this creates a direct learning-data poisoning surface
- the endpoint can also be used to flood the bounded learning bus and evict legitimate events
- because the body is unbounded here, it is also a memory-pressure/CPU-abuse surface

Recommended mitigation:

- require a signed one-time token tied to the originating request before accepting a callback
- derive the server verdict from server-side request state, not from client echo
- add request size limits, content-type validation, and host-level rate limiting
- segregate client-side telemetry from the main learning bus unless it is authenticated

### 4. Medium: demo mode is the default startup mode and exposes the learning endpoint

Evidence:

- `Mostlylucid.BotDetection.Console/Program.cs:149-152`
- `Mostlylucid.BotDetection.Console/Program.cs:573-678`

What happens:

- the default mode is `demo`
- demo/learning mode exposes `/stylobot-learning/{**path}`
- demo mode is also described as full logging / no blocking in the shipped config

Why this matters:

- the easiest way to start Stylobot is also the least safe way to expose it to the internet
- the learning endpoint fingerprints the deployment, returns synthetic responses, and creates unnecessary public attack surface
- operators can believe "it is running" while still being in a non-enforcing mode

Recommended mitigation:

- force an explicit `--mode` on first-run or default to `production`
- show a startup warning banner and non-zero exit unless the operator confirms `demo`
- never expose `/stylobot-learning/**` outside deliberate lab/testing deployments

### 5. Medium: observability endpoints are public and `/health` leaks internal topology

Evidence:

- `Mostlylucid.BotDetection.Console/Program.cs:552-557`

What happens:

- `/health` returns `mode`, `upstream`, and `port`
- `/metrics` is mapped without authentication or network restriction in the host

Why this matters:

- `/health` leaks internal routing details and operating mode
- `/metrics` provides an attacker with detector names, traffic shape, and timing/volume data useful for tuning attacks

Recommended mitigation:

- make `/health` return a minimal boolean status only
- keep `/metrics` on a separate listener, localhost-only binding, or trusted network allowlist
- if public scrape is required, put it behind separate auth or network ACLs

### 6. Medium: outbound feed/model dependencies are unsigned or unpinned

Evidence:

- `Mostlylucid.BotDetection/Data/BotListFetcher.cs:123-220`
- `Mostlylucid.BotDetection/Similarity/OnnxEmbeddingProvider.cs:146-220`

What happens:

- bot/user-agent patterns are downloaded from remote URLs at runtime
- ONNX embedding models and vocab are auto-downloaded from HuggingFace on first use
- there is no checksum pinning, signature verification, or mirror requirement in these code paths

Why this matters:

- remote compromise or supply-chain poisoning changes detection behavior
- these downloads create surprise egress and can break "self-hosted / no phone-home" expectations
- first-start behavior becomes network-dependent

Recommended mitigation:

- support signed, versioned local mirrors for all feeds and models
- require explicit opt-in before any runtime download
- pin checksums or signatures for downloaded artifacts
- add a strict "offline / zero phone-home" mode that fails closed on unexpected egress

### 7. Medium: optional enrichment and LLM features can exfiltrate request-derived data

Evidence:

- `Mostlylucid.BotDetection/Services/ProjectHoneypotLookupService.cs:46-60`
- `Mostlylucid.BotDetection/Orchestration/BlackboardOrchestrator.cs:1188-1218`
- `Mostlylucid.BotDetection/Orchestration/BlackboardOrchestrator.cs:1239-1273`
- `Mostlylucid.BotDetection/Services/IntentClassificationCoordinator.cs:126-133`
- `Mostlylucid.BotDetection/Services/IntentClassificationRequest.cs:31-67`

What happens:

- Project Honeypot queries include visitor IP-derived data in DNS lookups
- LLM classification snapshots include UA, path, header shape, cookie presence, and top detector findings
- intent classification sends session summaries that can include attack categories and response-history features

Why this matters:

- cloud/off-box analysis can become a data-governance problem even when the core product is self-hosted
- some customers will require strict no-egress operation

Recommended mitigation:

- make all outbound enrichers explicit and separately toggleable
- document exactly what data leaves the process for each provider
- add a zero-phone-home profile that disables remote feeds, remote LLMs, and third-party enrichers
- for threat intelligence specifically, support signed local ingestion and local-only processing as a first-class mode

### 8. Library-only: browser fingerprint token binding trusts raw forwarding headers

Evidence:

- `Mostlylucid.BotDetection/ClientSide/BrowserTokenService.cs:205-223`

What happens:

- token IP binding reads `CF-Connecting-IP`, `X-Forwarded-For`, and `X-Real-IP` directly
- it does not rely on `RemoteIpAddress` after trusted-proxy processing

Why this matters:

- on direct internet-facing deployments, a client can spoof these headers
- that weakens the token's IP binding and any downstream fingerprint correlation keyed to it

Recommended mitigation:

- only use `context.Connection.RemoteIpAddress` after `UseForwardedHeaders()` has normalized it
- if raw proxy headers must be consulted, only do so after trusted proxy validation

## Systemic Risks

These are not single bugs, but they matter operationally:

- online learning can be adversarially influenced by traffic; high-impact feedback should prefer authenticated operator feedback, approved fingerprints, or signed threat-intel ingestion
- bounded channels use `DropOldest` in several places; that is good for survivability, but it means noisy attackers can displace higher-value learning work
- if remote Qdrant or cloud LLMs are enabled, they should be treated as data processors and network dependencies, not just feature toggles

## Positive Controls Already Present

The codebase also contains meaningful hardening that should be kept:

- trusted proxy handling is now explicit instead of globally trusting all forwarded headers: `Mostlylucid.BotDetection.Console/Program.cs:322-373`
- upstream-trust mode can validate HMAC-signed detection headers instead of blindly trusting them: `Mostlylucid.BotDetection/Middleware/BotDetectionMiddleware.cs:1382-1441`
- challenge return URLs are sanitized against open redirects: `Mostlylucid.BotDetection/Endpoints/ChallengeEndpoints.cs:138-156`
- browser fingerprint tokens include signing, expiry, and replay prevention when the fingerprint endpoint is used: `Mostlylucid.BotDetection/ClientSide/BrowserTokenService.cs:78-178`
- API keys are constant-time compared and can be constrained by path, time window, and rate limit: `Mostlylucid.BotDetection/Services/InMemoryApiKeyStore.cs:46-118`

## Hardening Status

Completed in the current tree:

1. Stop stripping CSP / X-Frame-Options and stop leaking detection verdict headers to clients by default.
2. Bind challenge tokens to the solver context, not just expiry.
3. Lock down `/api/bot-detection/client-result` with signed request tokens, size limits, and rate limits.
4. Make `production` the safe default.
5. Move `/metrics` and detailed `/health` behind safer defaults.

Still worth doing next:

1. Add explicit host-level rate limiting for the demo callback endpoints.
2. Add checksum/signature pinning for any operator-enabled remote feed or model download path.
3. Review remaining optional outbound features (`VerifiedBotRegistry`, remote LLMs, Project Honeypot) under a single zero-phone-home profile.
6. Add a documented offline / zero-phone-home mode for feeds, threat intel, models, and LLMs.
7. Keep remote enrichers optional and clearly document the exact egress each one performs.

## Bottom Line

Stylobot's core detection pipeline is materially stronger than its auxiliary surfaces. The biggest current weaknesses are not detector quality; they are around response/header behavior, public debug or learning endpoints, bearer-style challenge bypass, and surprise egress. If those are tightened, the runtime posture improves substantially without changing the detector stack itself.
