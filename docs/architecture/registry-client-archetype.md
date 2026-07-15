# Registry-client archetype — detecting legit container-registry traffic without a bypass

> **Status:** design sketch (foss- lane). Prompted by standing up `harbor.stylo.bot` as a gateway
> upstream: docker/OCI registry clients are machine callers that must NOT be tarpitted — but the fix
> is in **detection**, never an EndpointPolicy `allow`/skip-detection/trusted-path.

## Problem

`harbor.stylo.bot` (customer container registry) is fronted by the StyloBot gateway (dogfood: the
gateway is the reverse proxy + detection + TLS edge). `docker` / `containerd` / `podman` / `helm` /
`skopeo` / `buildkit` clients pulling+pushing images are **machine callers with no browser
fingerprint** doing many rapid requests. Naive detection reads that as bot-shaped and could
throttle/challenge them — but they are legitimate customers pulling their licensed images.

## Confirmed live (2026-07-15) — the tarpit is real, not theoretical

Docker-shaped traffic (`User-Agent: docker/24.0.7 …`, `Accept: …manifest.v2+json`) to
`https://harbor.stylo.bot/v2/` through the prod gateway, no debug key:
- Every `/v2/` ping → **`risk=0.90, type=Unknown`** → action `throttle-stealth`, **2–4s per request**
  (gateway log: `[ACTION] Executing action policy 'throttle-stealth' for /v2/ (risk=0.90, type=Unknown)`).
- `/api/v2.0/systeminfo` → **429**.

So a real `docker pull` (dozens of manifest+blob requests) would crawl or fail. Detection reads the
registry client as a high-risk Unknown bot. That is precisely what the `RegistryClient` archetype
below must fix — by making the *score* correct (Safe/low), not by telling the gateway to stop looking.

## Hard constraint (the whole point — [[feedback_no_stylobot_bypasses_for_detection_issues]])

The fix is **in detection**, never a `/v2/* → allow` EndpointPolicy. StyloBot *is* the detection
engine; "don't look at these paths" is banned (it's the same class as the Stripe-webhook / NuGet-pull
case). Detection must **look** at registry traffic, **recognize** it as a legitimate machine
archetype, and **score** it low-threat — while a UA-spoofing scraper that isn't actually doing the
registry protocol still gets caught. Rate limits are still fine (a rate policy is not a bypass).

## Shape: a `RegistryClient` archetype (a Safe cluster), seeded not hard-coded

Per [[feedback_centroids_not_rules]] + [[project_archetype_anchor_safe_cluster]]: registry clients
are a machine **archetype** — a `BotClusterType.Safe` centroid that thin signatures anchor to and
self-tune from observed traffic. Not a hard-coded UA switch. Thin sigs start at the archetype (the
prior) and drift; the archetype is the anchor, self-tuning is the drift.

### Anchoring signals (the `.archetype.yaml` seed — context, not the runtime rule)

No single signal is sufficient; spoof-resistance comes from the **behavioral combination**:

1. **Client-family (UA hint, Model 2)** — `docker/x.y go/…`, `containerd/…`, `Go-http-client`
   (the registry client lib), `Helm/3`, `skopeo`, `buildkit`, `podman`, `crane`, `oras`. A UA-family
   *seed list* in the manifest — the SEED for the centroid, never the runtime decision.
2. **Protocol-sequence (behavioral truth, on the atom)** — the deterministic Registry v2 sequence:
   `GET /v2/` (ping → 401) → `GET /service/token?scope=repository:<repo>:pull` → `GET
   /v2/<repo>/manifests/<ref>` → `GET /v2/<repo>/blobs/<digest>`. An ordered behavioral molecule
   across the session (an `EphemeralKeyedWorkCoordinator` keyed by session/signature observes it).
3. **Content-negotiation** — registry Accept media types: `application/vnd.docker.distribution.
   manifest.v2+json`, `application/vnd.oci.image.manifest.v1+json`, `…image.index.v1+json`, blob types.
4. **Auth-flow** — the bearer dance: `401 WWW-Authenticate: Bearer realm=…` → token endpoint →
   authenticated `/v2/` with `Authorization: Bearer <jwt>`.
5. **Endpoint-shape** — the host's inferred endpoint shape is *registry API* (per the per-domain
   inferred-shape / `EndpointClassifier` work, [[project_topology_signal_tiers_inferred_shape]]):
   `/v2/*` is machine-API, not content/resource.

### Why this is NOT a bypass (spoof resistance)

- Detection **still runs** on every registry request — scored, observed, logged, learned-from. The
  archetype changes the **score** (legit machine → low ThreatBand/RiskProfile), not whether we look.
- Classification is **nearest-centroid over the full signal set + drift** — not the UA alone. A
  scraper spoofing `docker/24` but not doing the `/v2/` sequence, not sending manifest Accept
  headers, not following the auth flow → drifts away from the `RegistryClient` centroid → does NOT
  get Safe → gets normal detection (tarpit if it behaves like a scraper). The UA is a hint; the
  ordered protocol is the truth.

## Verdict composition ([[project_signature_risk_verdict]])

The archetype folds into the unified `SignatureRiskVerdict`, composed once at read:
- **BotType:** `RegistryClient` (a Safe machine archetype, sibling to GoodBot/Tool).
- **ThreatBand:** low. **RiskProfile:** machine-legit (not human, not threat).
- **Justification:** "registry protocol client (docker/OCI v2) on a registry endpoint; authenticated;
  behaviour matches the RegistryClient centroid."
- **Action:** the low-threat verdict means the action policy simply doesn't enforce block/throttle/
  challenge — **without** a skip-detection marker. Enforcement is absent because the *score* is low,
  not because we told the gateway to look away.

## Integration (atoms / StyloFlow — [[reference_stylobot_intended_architecture]])

- `RegistryClientSensor` (SensorAtom) extracts signals 1,3,4 per request into the `SignalSink`
  (always raise `registry.client.ran`; value hints only when matched — UA family, accept-media,
  auth-present). PII rule: hold the raw UA/token internally on the atom, raise only "I have X".
- The **protocol-sequence** (signal 2) is a cross-request molecule keyed by session/signature.
- A ConstrainerAtom/RankerAtom classifies against the `RegistryClient` centroid (nearest-centroid +
  Mahalanobis novelty gate — [[reference_centroid_delta_streaming_absorption]]).
- The `EndpointClassifier` contributes the *registry API* endpoint shape (signal 5).
- Seed via a `registry-client.archetype.yaml` StyloFlow manifest (UA families + protocol steps +
  accept-media), loaded through `IConfigProvider` (appsettings → YAML → defaults). No hard-coded
  lists in C# ([[feedback_no_word_lists]]).

## Configurable ([[feedback_all_settings_configurable]])

Seed UA families / accept-media / protocol-step defs (in the `.archetype.yaml`); the RegistryClient
centroid novelty+drift thresholds; the ThreatBand/RiskProfile mapping for the archetype; a
confidence ladder (strict = requires the behavioural sequence; lenient = UA + endpoint shape).

## Rate limiting still applies

A legit-but-abusive client (hammering pulls) is **rate-limited** via the normal rate policy —
orthogonal to archetype recognition, and explicitly allowed by the no-bypass rule. So "recognized as
legit" ≠ "unlimited"; it means "not treated as a hostile bot".

## Implementation plan (concrete — real FOSS types)

Maps onto the existing machinery; no new subsystem:

1. **Seed the client family** — add docker/OCI registry clients to the well-known-bots catalog as
   `BotType.Tool` (`Models/BotDetectionResult.cs`; `Tool` + internal already renders "Internal", and
   `Tool` is a friendly category). Entry per `Definitions/WellKnownBots/WellKnownBotEntry.cs` →
   `well-known-bots.baseline.json` (UA patterns: `docker/`, `containerd/`, `Helm/`, `skopeo`,
   `buildkit`, `podman`, `crane`, `oras`, `Go-http-client` scoped to `/v2/`). This is the SEED only.
2. **Behavioral sensor** — a `RegistryClientSensor` (native `IDetectorAtom`, registered via
   `AddDetectorAtom<T>` in `BotDetectionModule`) that raises into the `SignalSink`:
   `registry.v2.ran` always; hints for `registry.v2.step` (ping/token/manifest/blob),
   `registry.accept.manifest`, `registry.auth.bearer`. The ordered v2 sequence is a per-session
   molecule (`EphemeralKeyedWorkCoordinator` keyed by signature).
3. **Archetype/centroid** — register a `RegistryClient` archetype in `IdentityArchetypeRegistry`
   (`Identity/IdentityArchetypeRegistry.cs`) with an `IdentityVectorLayout` slice for the registry
   signals; classify nearest-centroid + drift (the existing centroid path). Anchor = the seed; the
   centroid self-tunes from real registry traffic.
4. **Spoof resistance is already the model** — `Models/BotTypeClassification.cs` already encodes
   "above the confidence threshold, even a GoodBot UA earns standard treatment". So a `docker/24` UA
   *without* the corroborating `registry.v2.*` behavioral signals does NOT reach the friendly branch —
   it's scored normally. Reuse that threshold; do not add a UA-only fast-path.
5. **Action mapping** — map `BotType.Tool`/RegistryClient to a low-threat action (no
   `throttle-stealth`) via the same mechanism as `BotDetectionOptions.InternalNetworkBotTypeActionPolicies`
   (a per-BotType action-policy map). Rate limits still apply on top (allowed, not a bypass).
6. **Config** — seed families + accept-media + protocol steps in a `registry-client.archetype.yaml`
   (StyloFlow manifest, `IConfigProvider` three-tier); thresholds on options classes
   (`WellKnownBotsOptions` / a new `RegistryClientOptions`). No hard-coded lists in C#.

## Validation

Once `harbor.stylo.bot` DNS is live: a real `docker login` + `docker pull` from an external client,
watching the gateway action-policy log. Confirm (a) the request is **detected** (scored+logged, not
skipped), (b) classified `RegistryClient`/Safe (low threat), (c) not tarpitted. Then a **spoof test**:
a scraper sending `User-Agent: docker/24` but doing scrape-shaped requests → must NOT get Safe (it
drifts from the centroid) → normal enforcement. That negative test is the proof it isn't a bypass.
