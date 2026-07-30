# Webhook recognition + policy — design

> **Status:** design (stylobot- lane). Makes StyloBot *aware* of inbound webhook traffic as a
> legitimate machine class and gives it a correct policy — recognize + score low, never a path
> allowlist. Mirrors the RegistryClient archetype (`docs/architecture/registry-client-archetype.md`)
> and obeys the hard rule: **fix detection, never bypass it.**

## Problem

Inbound webhooks (Stripe, GitHub, Shopify, Slack, …) are high-volume machine-to-machine POSTs with
no browser fingerprint. Naive detection reads them as bot-shaped and would throttle/tarpit/challenge
them — breaking legitimate integrations. Today StyloBot has **no webhook awareness**: the arcjet
catalog maps a few named webhook-sender UAs to `BotType.GoodBot`, and the only "webhook" handling in
config is an *example* of the banned pattern (`/api/webhooks/*: allow`, a path skip). There is no
behavioral recognition and no policy.

The fix is the same class as RegistryClient: **recognize the webhook machine archetype and score it
low-threat** — while a spoofer POSTing to the same endpoint still gets normal detection. Recognition
is **per-request**, never a per-path allowlist.

## Hard constraint (no bypass)

Detection **always runs** on webhook traffic — scored, logged, learned-from. Recognition changes the
**score/action** for a *corroborated* webhook, never whether we look. No `/webhooks/* → allow`
config, no skip-path, no trusting a spoofable header on its own. The negative test (a POST to a
webhook endpoint without the corroborating signals is scored normally) is the proof it isn't a bypass.

## Recognition — layered, corroboration-based

A request is recognized as a legitimate webhook when it has the **behavioral webhook shape** AND at
least one **corroborator**. No single signal grants trust.

**Behavioral shape (the base signal):**
- HTTP `POST` (webhooks are deliveries, not reads), and
- a JSON (or form) body content-type, and
- the presence of a webhook **signature/event header** — detected by header *name* from a seed list
  (`Stripe-Signature`, `X-Hub-Signature-256`, `X-GitHub-Event` / `X-GitHub-Delivery`,
  `X-Shopify-Hmac-Sha256`, `X-Slack-Signature`, `X-Webhook-Signature`, …). We detect **presence**, not
  validity — we do not hold the signing secret; the *receiver* validates it (see verification signal).

**Corroborators (confidence order — highest first):**
1. **Verified track record (receiver-attested, strongest).** The source IP has a consistent history of
   **2xx** upstream responses to this endpoint. StyloBot is the reverse proxy, so it observes the
   receiver's own verdict: `2xx` = the app verified the HMAC and accepted the delivery; `4xx`
   (esp. 400/401/403) = invalid signature / rejected. This is ground truth we get for free. A source
   whose deliveries consistently 2xx is a verified legit sender; one accruing 4xx is failing
   verification (spoof/suspect). **Captured post-`_next`** — the status is only known after the
   upstream responds, so it is a *learning* signal that corroborates the *next* request, not an inline
   gate for the current one. (Explicitly record status AFTER `_next`, per the status-code-pre-`_next`
   regression.)
2. **Named provider.** A provider seed (name → signature header + optional published IP ranges) matches
   — like the RegistryClient family list. Raises confidence for known senders.
3. **Dominant / stable source IP ("commonest IP").** The source IP is in the receiver endpoint's
   *learned* dominant/stable IP set. Corroborating only.

**Recognition fires when:** behavioral shape **AND** (verified-track-record OR named-provider OR
established-dominant-IP). A POST with a signature-shaped header from a **new/rare IP**, no named
provider, and no 2xx history → **not** recognized → scored normally. Spoofers self-select out: their
invalid deliveries get 4xx from the receiver and never accrue a verified record no matter the volume,
so "commonest IP" effectively means "commonest *verified* IP."

**On recognition** the sensor raises a strong **negative-delta contribution** (low threat,
`BotType.GoodBot`, **no early-exit** — detection still runs and learns), plus `webhook.*` signals.

## Learning stores (SQLite — no in-memory)

`WebhookEndpointReputation` (persisted, `webhooks.db` or a table in the existing identity/session DB):
per **(learned receiver-endpoint, source-IP)** row tracking:
- webhook-shaped request count (dominance / "commonest IP"),
- upstream response-status tallies (2xx verified count, 4xx failed count) → the verified track record,
- first/last seen, decay.

The **receiver-endpoint is learned, not configured** — a path that receives webhook-shaped POSTs from a
dominant, 2xx-verified source becomes a recognized webhook receiver over time (endpoint-shape
inference, cf. `EndpointClassifier`). No hard-coded paths. New endpoints/IPs get normal detection until
they establish a record.

## Policy — "shape only the unrecognized", with a high safety ceiling

- **Recognized webhook** (per-request, corroborated) → **never** throttled / tarpitted / challenged, and
  never rate-shaped for normal volume. Correct low score → no enforcement (like RegistryClient), plus an
  explicit `webhook-recognized` benign route in `PostDetectionActionGate` so it is never shaped.
  **Legit steady traffic is never slowed.** The one thing that *does* still apply is a **high safety
  ceiling** — a rate cap set far above any real webhook volume, so a *compromised-but-recognized* sender
  (or a recognition mistake) can still be shed at absolute-flood levels. Legit volume never reaches the
  ceiling, so "never slow legit use" holds; the ceiling only ever bites a genuine flood.
- **Site-wide ceiling (operator directive):** the high safety ceiling is a **site-wide** primitive, not
  webhook-specific — *every* endpoint, including otherwise-trusted traffic, gets the same absolute-flood
  ceiling. Webhook recognition simply means the ceiling is the **only** shaping applied to recognized
  traffic (everything else — throttle/challenge/tarpit — is suppressed). Implement the ceiling as a
  shared, configurable primitive (a very high default RPM) that the webhook path reuses rather than a
  bespoke webhook cap.
- **Unrecognized / suspicious** traffic to the *same* endpoint → normal detection + the endpoint's
  rate-limit / challenge (below the site ceiling). The endpoint is protected from floods and spoofers
  **without a blanket path allowlist** — recognition is per-request.

## Future config seams (design now, build later — not in this cut)

Leave clean `EndpointPolicy`-style seams (like `TransportTrust:TrustedProxyIps`) so these drop in with
no rework:
- **Per-endpoint permitted source IPs** — an optional operator allowlist of legit sender IPs/ranges
  (operator intent, strengthens recognition; not a detection skip).
- **Strict mode** — "recognize *only* permitted IPs" and/or "recognize *only* senders with a verified
  (2xx) track record."

(The high safety ceiling is **not** a future seam — it ships in this cut as a site-wide primitive; see
the Policy section.)

## Components / files (RegistryClient 5-file pattern + trackers)

1. **`Orchestration/Atoms/WebhookSensor.cs`** — `DetectorAtomBase`, Wave 0, reads request via
   `IHttpContextAccessor`; recognizes behavioral shape + corroborators; emits the negative-delta
   contribution + `webhook.*` signals. All tunables via `IDetectorConfigProvider` / the YAML.
2. **`Definitions/Webhooks/webhook.archetype.yaml`** — seed: signature-header names, named providers
   (name → header + optional IP ranges), scoring knobs (corroborated_confidence_delta, weight,
   verified-2xx threshold, dominance threshold, decay). No hard-coded lists in C#.
3. **`Models/DetectionContext.cs`** — `SignalKeys.Webhook*` (webhook.detected, webhook.shape,
   webhook.provider, webhook.ip_dominant, webhook.verified_record, webhook.endpoint).
4. **DI registration** in `BotDetectionOrchestrator.cs`.
5. **`Mostlylucid.BotDetection.UI/Services/DetectionNarrativeBuilder.cs`** — friendly name + category.
6. **`Reputation/WebhookEndpointReputation.cs` + `Data/SqliteWebhookReputationStore.cs`** — the
   per-(endpoint, IP) dominance + verified-status store; a post-`_next` recorder that writes the
   upstream status into the store (middleware seam, after `_next`).
7. **`Enforcement/PostDetectionActionGate.cs`** — a `webhook-recognized` benign-route arm (mirrors the
   registry-client / verified-crawler arms), keyed on the corroboration signal, never on shape alone.
   For recognized traffic it suppresses throttle/challenge/tarpit and leaves only the site ceiling.
8. **Site-wide safety ceiling** — a shared, configurable high-RPM ceiling primitive (a new option, e.g.
   `BotDetection:SafetyCeilingRpm`, very high default) applied to every endpoint including trusted/
   recognized traffic. Reuse the existing token-bucket (`ITokenBucketStore`) rather than a bespoke cap;
   the webhook benign route lets this ceiling through while suppressing everything else.

## Testing

- **Recognition unit tests** (mirror `RegistryClientSensorTests`): POST + signature header + dominant/
  verified source → recognized (negative delta, GoodBot, no early-exit). **Negative/spoof test
  (load-bearing):** POST to the same endpoint with a signature-shaped header from a new/rare IP, no
  named provider, no 2xx history → **not** recognized → scored normally (proof of no bypass).
- **Verified-record test:** a source with consistent 2xx accrues the verified record and is recognized;
  a source accruing 4xx is not (and stays scored normally regardless of volume).
- **Post-`_next` ordering test:** the upstream status is recorded AFTER `_next`, not before.
- **Policy test** (`PostDetectionActionGate`): recognized webhook → benign route / not throttled or
  challenged; unrecognized traffic to the same endpoint → normal action (rate-limit/challenge) still
  applies.
- **Safety-ceiling test:** recognized traffic below the ceiling is never shaped; recognized traffic
  driven above the site ceiling (absolute flood) IS shed — proving "never slow legit use" holds while a
  compromised-but-recognized sender can still be capped. The ceiling applies site-wide (a non-webhook
  endpoint over the ceiling is also shed).
- **Persistence test:** the endpoint/IP reputation survives restart (SQLite), no in-memory store.

## Validation

Replay a Stripe/GitHub-style webhook sequence (POST + signature header, stable source IP, upstream
returns 2xx) through the pipeline: confirm (a) it is **detected** (scored + logged, not skipped),
(b) recognized as a webhook (low threat, GoodBot), (c) never throttled. Then the **spoof test**: POST
to the same endpoint with a forged signature header from a new IP, upstream returns 4xx → must **not**
be recognized (no negative delta), gets normal detection + the endpoint's rate-limit. That negative
test is the proof it isn't a bypass.
