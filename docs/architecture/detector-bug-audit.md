# Detector Bug Audit

Full audit of every detector in `Mostlylucid.BotDetection` for the bug class surfaced by the
VPN-detector incident (`geo.is_vpn` hardcoded false — MaxMind Anonymous IP DB never wired):

1. **Hardcoded false/true returns** — always the same value regardless of input
2. **Dead placeholders** — stubs, TODO-gated functionality, placeholder implementations
3. **Hardcoded word lists** — string arrays, prefix dictionaries, ASN lists that belong in YAML seeds
4. **Unwired seams** — interfaces with no registered implementation, options that default to inert
5. **Silent failures** — swallowed exceptions that mask real errors, failures that degrade to "clean"
6. **Never-reached code paths** — implemented code nothing runs

Audited: `Detectors/` (all 10), `Orchestration/Atoms/` (all, incl. orchestrator/registration),
`ThreatIntel/`, `Services/`, `Identity/`, gateway wiring (`Stylobot.Gateway`),
`Mostlylucid.GeoDetection`, and the stylobot-commercial repo (to distinguish genuine misses from
intentional commercial seams).

> **Re-audit pass (2026-08-04):** each finding was re-checked against the atom pipeline — if an
> atom now owns that detection surface, or the commercial repo registers the implementation, the
> finding is an **atom-migration / commercial-seam artefact**, not a bug. Genuine findings are
> marked `[GENUINE]`; artefacts are listed in the separate migration-artefacts section.

Severity key: **HIGH** = detection surface dead in production deployments / wrong verdicts
possible; **MED** = degraded detection, silent; **LOW** = maintenance debt, invisible unless touched.

---

## GENUINE findings

### G1 [HIGH] Transport-fingerprint signals never reach the atoms in the reference gateway

`Stylobot.Gateway/Transforms/TlsFingerprintingTransform.cs:44-102` +
`Orchestration/Atoms/TlsFingerprintAtom.cs:186-204` + `Http2FingerprintAtom.cs:167-173,231-267` +
`TcpIpFingerprintAtom.cs:193-245`

The gateway's middleware order is `UseTlsMetadataCapture()` → `UseGeoRouting()` →
`UseBotDetection()` → `MapReverseProxy()` (`Stylobot.Gateway/Program.cs:352-435`). The fingerprint
transform runs **inside YARP at proxy time** and writes `X-TLS-Protocol`, `X-TLS-Cipher`,
`X-HTTP-Protocol`, `X-TCP-*`, `X-IP-*` headers to the **outbound proxy request** — after detection
has already run. The atoms read **inbound `Request.Headers`** for the same names, so:

- `TlsFingerprintAtom` never sees `X-TLS-Protocol`/`X-TLS-Cipher` → `tls.protocol`,
  `tls.cipher_suite` never raised → the outdated-SSL / weak-cipher / old-TLS arms never fire.
  The data captured by `TlsMetadataMiddleware` into `HttpContext.Items` is consumed by no one in
  `Mostlylucid.BotDetection` (verified: zero readers of `GatewayHttpContextKeys.TlsProtocol`).
- `X-JA3-Hash` / `X-JA4*` / `X-HTTP2-*` / `X-TCP-*` / `X-IP-*` are injected by **nothing** in the
  reference deployment (the transform itself notes SETTINGS/TCP capture needs nginx/HAProxy/Kestrel
  hooks). JA3/JA4 matching, HTTP/2 SETTINGS fingerprinting, pseudoheader order, stream priority,
  WINDOW_UPDATE, PUSH, preface validity, TCP window/TTL/options/MSS, DF-flag — all inert.
- Only what survives: `req.Protocol`-derived HTTP/2-vs-HTTP/1 population norms and the
  Connection-header checks.

**Fix direction:** raise the captured metadata from `HttpContext.Items` in the atoms (or a
pre-detection middleware), or run the transform before detection and copy onto the inbound request.

### G2 [HIGH] `IJa3ReferenceIndex` (TLS reference corpus) is never registered — genuine miss

`Definitions/TlsReference/Ja3ReferenceIndex.cs:14` + `TlsFingerprintAtom.cs:71`
(`IJa3ReferenceIndex? referenceIndex = null`)

`Ja3ReferenceIndex` loads the embedded `tls-reference-corpus.yaml` in its constructor and powers
the cipher-subset (damru) + version-delta (Multilogin/Kameleo) checks in `TlsFingerprintAtom.
RunCorpusChecks` — but **nothing registers it in DI in either repo** (FOSS or commercial; only
tests instantiate it). `_referenceIndex` is always null → `RunCorpusChecks` returns immediately.
The corpus infra (embedded YAML baseline, signed refresh service, options, tests) all ships, so
this is a wiring gap, not a descope. Practical impact is additionally gated behind G1 (JA3 headers
never reach the atoms), so fixing G1 alone doesn't activate the corpus — both must land.
**Fix:** `services.TryAddSingleton<IJa3ReferenceIndex, Ja3ReferenceIndex>()` — one line.

### G3 [MED] `AsnLookupService` is never registered — genuine miss

`Services/AsnLookupService.cs:55` (zero DI registrations repo-wide, FOSS and commercial) +
`IpAtom.cs:78` (`IAsnLookupService? asnLookup = null`)

`IpAtom` documents a three-layer datacenter classification: prefix hints → **authoritative ASN
lookup** → dynamic CIDR, "ASN overrides prefix guesses in both directions". Layer 2
(`AsnLookupService`, full Team Cymru implementation) has **no registration anywhere** — every
deployment resolves null and silently falls back to prefix/CIDR heuristics. Consequences:
`ip.asn`, `ip.asn_org`, `ip.is_isp` signals never raised; the residential-ISP human signal (−0.15)
never fires. Datacenter detection still works via prefixes/CIDR, so this degrades rather than
kills — but the ISP/residential classification surface is entirely dead.
**Fix:** register `IAsnLookupService → AsnLookupService`, then move `KnownDatacenterAsns` to YAML (A2).

### G4 [MED] `IClusterStore` never registered in FOSS — genuine miss (commercial expects FOSS to wire it)

`Services/NullClusterStore.cs` + `Services/SqliteClusterStore.cs` + `BotClusterService.cs:48,76`

The **commercial repo's own comment proves the FOSS default was supposed to exist**:
`Stylobot.Commercial.Persistence.Postgres/ServiceCollectionExtensions.cs:290`
("FOSS exposes IClusterStore -> SqliteClusterStore; we swap to the Postgres store" +
`RemoveAll<IClusterStore>()`). But FOSS registers **neither** `NullClusterStore` nor
`SqliteClusterStore`. In FOSS deployments cluster hydration/persistence/label write-through are
all dead (state is in-memory only, rebuilt each restart); the commercial swap works only because
it registers Postgres unconditionally. The documented FOSS default is missing.
**Fix:** `TryAddSingleton<IClusterStore, SqliteClusterStore>()` (or NullClusterStore).

### G5 [MED] `ResponseCoordinator` never registered — response-behavior arm dead

`Orchestration/ResponseCoordinator.cs:219` (class + `ResponseCoordinatorOptions` at :55 exist;
zero DI registrations in either repo) + `Orchestration/Atoms/ResponseBehaviorAtom.cs:139`

`ResponseBehaviorAtom` degrades explicitly (raises `response.coordinator_available:false` and
returns an Info), but the ENTIRE analysis — 404-scan tiers, fail2ban escalations, auth
brute-force ladder, error harvesting, rate-limit tiers, honeypot history, response score — never
runs. All `response.*` signals (including `response.auth_failures`, which `AccountTakeoverAtom`
consumes) are absent. Policy docs (`DetectionPolicyConfiguration.cs:42,117`) and an options
surface (`HoneypotPaths` etc.) reference a coordinator no host wires. Semi-audible (one
`available:false` signal) but the detection surface is dead.
**Fix:** register `ResponseCoordinator` (IAsyncDisposable, singleton) or descope the atom cleanly.

### G6 [HIGH] ThreatIntel per-provider `Enabled` flags are decorative — disabled providers still fetch + answer

`ThreatIntel/ThreatIntelCoordinator.cs:25` (`_enabled = ti.Enabled && providers.Count > 0`) +
`Modules/BotDetectionModule.cs:618-625` (all four providers registered unconditionally) +
`ThreatIntelOfflineProviderBase.cs:30,79-104` (`IsConfiguredEnabled` consulted only by
`GetStatus`) + `CisaKevProvider.cs:49,84-114` / `CloudRangesProvider.cs:55,95-153`

`IsConfiguredEnabled` gates **nothing** except dashboard status display: `RefreshAsync` and
`TryLookup` never check it. With the master `ThreatIntel:Enabled` switch on, a provider configured
`Enabled: false` still fetches its feed on the refresh cadence and still answers lookups
(Spamhaus/Tor/CISA/CloudRanges), contradicting the module comment ("each provider self-gates on
its Enabled flag"). The dashboard shows `Enabled=false` while the provider keeps firing — operators
are actively misled.
**Fix:** gate `RefreshAsync`/`TryLookup` on `IsConfiguredEnabled`, and filter providers in refresh
scheduling.

### G7 [HIGH] `TlsFingerprintAtom` known-fingerprint sets contain fabricated data — both arms can never match

`Orchestration/Atoms/TlsFingerprintAtom.cs:42-59`

`KnownBotFingerprints` and `KnownBrowserFingerprints` are sequential placeholder hex strings
(`"a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"`, `"b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9"`, ...) — not real
JA3 hashes of any client. The bot arm (+0.85, weight 1.8) and the browser arm (−0.7 human
attestation) can never match real traffic. The browser arm is the costly loss: a strong
transport-level human attestation that never fires. (Compounded by G1: JA3 headers aren't even
injected in the gateway.)
**Fix:** seed from real JA3 data or the TLS corpus G2 unlocks — never invented values.

### G8 [MED] `AiAtom` is a placeholder that fabricates "AI analysis" evidence

`Orchestration/Atoms/AiAtom.cs:81-98`

The AI atom (Priority 100) runs when risk ≥ 0.5 and ≥ 2 detectors contributed, then
`await Task.Delay(10)` and returns a contribution reading "AI analysis confirms high-risk signals"
(+0.2) when risk is already > 0.8, or an Info otherwise. **No AI runs.** The contribution is
misleading evidence in the ledger (an operator reading "AI analysis confirms" believes an LLM/ONNX
model ran). Note the +0.2 boost on an already-high-risk request is a feedback push over enforcement
thresholds. (Commercial `LlmAtom` exists separately; this atom is distinct and dead.)
**Fix:** wire a real model, or rename the contribution ("risk confirmation") and remove the fake
reason text.

### G9 [MED] `IntentClassificationCoordinator` never registered — LLM intent arm permanently dead

`Orchestration/Atoms/IntentAtom.cs:53` + `Scheduling/BotDetectionHostedSingletonsBootstrap.cs:95`
(the "eager-resolve" is a null-safe `GetService` that resolves null)

The commercial LLM classifiers reference this coordinator by name and expect it live
(`Stylobot.Commercial.Llm.OpenAi/ServiceCollectionExtensions.cs:15`), but **neither repo registers
it**. The LLM intent enqueue arm (`IntentAtom.cs:133-158`) never runs; IntentAtom's heuristic path
still fires, so detection degrades silently. Genuine miss, not a seam.
**Fix:** `TryAddSingleton<IntentClassificationCoordinator>()` in FOSS (or the LLM pack).

### G10 [MED] `ISessionVectorSearch` never registered — session-vector HNSW arm dead

`Similarity/ISessionVectorSearch.cs` + `Similarity/SlimSessionVectorSearch.cs` (FOSS impl exists;
zero registrations in either repo) + `Orchestration/Atoms/SessionVectorAtom.cs:210`

SessionVectorAtom's voidness detection (:365-420) and trajectory-toward-attack-cluster (:422-466)
— both documented as core capabilities — never run, with no signal indicating the absence.
Genuine miss.
**Fix:** `TryAddSingleton<ISessionVectorSearch, SlimSessionVectorSearch>()` (or in the commercial
Postgres pack with its HNSW store).

### G11 [MED] `IBotClusterReader`/`IClusterMembershipLookup` have no bindings

`Services/BotClusterService.cs:34` (implements both interfaces, registered as concrete type only)
+ API consumers `ReadEndpoints.cs:385,484` (`[FromServices] IClusterMembershipLookup?` → null)

The endpoints take the interfaces as optional params and silently get null — cluster-lookup
features disappear from the API. Trivial fix: bind both interfaces to the concrete registration.
**Fix:** `TryAddSingleton<IBotClusterReader>(sp => sp.GetRequiredService<BotClusterService>())` etc.

### G12 [MED] `ProjectHoneypotLookupService` — outage becomes affirmative "not listed" evidence

`Services/ProjectHoneypotLookupService.cs:83-94` + `Orchestration/Atoms/ProjectHoneypotAtom.cs:98-107`

Any `SocketException` (timeout, resolver down, network blip) is treated as NXDOMAIN "not listed"
and **negative-cached for 30 min** (`IsListed = false`). The atom then raises
`honeypot.checked:true, honeypot.listed:false` — a Honeypot outage reads as affirmative clean
evidence. Also `Dns.GetHostAddressesAsync` is called with no timeout on the hot path — the exact
system-resolver stall `AsnLookupService.cs:246-250` documents and avoids.
**Fix:** distinguish NXDOMAIN from other failures; treat failure as "unknown", don't negative-cache.

### G13 [MED] `geo.is_vpn` — baseline case status

`Mostlylucid.GeoDetection/Services/IpApiGeoLocationService.cs:106` +
`MaxMindGeoLocationService.cs:27-35,169-202,268-317` + `Helpers/GeoLocationSignalEmitter.cs`

Current state (2026-08-04; `MaxMindGeoLocationService.cs` has an **uncommitted** WIP fix):

- **Gateway (IpApi provider):** `IsVpn = result.Proxy` — real data while ip-api is reachable.
  The geo emit is restored (`IpAtom.cs:109-118` → `GeoLocationSignalEmitter`), after the v8 atom
  refactor had dropped it and every consumer read false/absent. Consumers: `BotTypeFilter.cs:70`,
  `SignatureCoordinator.cs:404/841/879`, `ClickFraudAtom.cs:92`, `IdentityVectorAtom.cs:173`,
  `IdentityChangeAtom.cs:111`, `YarpExtensions.cs:149`, `UrlSignalProjection.cs:211`,
  `HttpContextExtensions.IsVpn()` — 8+ read sites.
- **Silent fabricated-geo downgrade:** gateway sets `FallbackToSimple = true`
  (`Stylobot.Gateway/Program.cs:293`). `SimpleGeoLocationService` (`IGeoLocationService.cs:29-90`)
  is a **mock** that invents country data from the IP's first octet ("for demo purposes") with
  `IsVpn` never set. When ip-api fails (rate limit, outage), every request gets fabricated
  country + `is_vpn=false` — a silent downgrade to fake data, not just "no data".
- **MaxMind path:** the WIP wires an `AnonymousDatabasePath` reader; without it, startup logs a
  warning ("VPN detection is INERT") — audible now, good. But `GeoLite2UpdateService` auto-download
  covers only the City DB, never the anonymous DB, so out-of-the-box MaxMind deployments remain
  inert until an operator places the file manually. `LookupCountry` (Country DB mode) never
  populates the anonymizer flags — silent false in that mode. Dead field `_anonymousReader`
  (`MaxMindGeoLocationService.cs:33`; only `_anonReader` is used).
- `GeoRoutingOptions.BlockVpns` defaults false (opt-in) — by design.

### G14 [MED] Two sources of truth for datacenter prefixes

`IpAtom.cs:48-58` (`DatacenterPrefixes` hardcoded dict, TODO-to-YAML) +
`Models/BotDetectionOptions.cs:722-728` (`DatacenterIpPrefixes` CIDR list) +
(third octet map in the removed `IpDetector`, dead code per A1)

Three overlapping hardcoded "which IPs are cloud" catalogs that disagree in scope (octet prefixes
vs CIDRs) and can drift independently. Also duplicates `AsnLookupService.KnownDatacenterAsns` (A2).
One source, YAML-seeded, required.

### G15 [MED] `BotClusterService` hardcodes null intent/threat and hardcodes fediverse names

`Services/BotClusterService.cs:674-675` (`IntentCategory = null, ThreatScore = 0.0` — "populated
when intent HNSW has data", no population path exists in this repo) +
`BotClusterService.cs:1254-1260` (hardcoded Mastodon/Pleroma/Misskey/... fediverse list for
`InferSafeLabel`, not YAML)

Cluster cards always show null intent / 0.0 threat; new fediverse software silently falls into
generic "Verified-Fanout".

### G16 [MED] `IpDetector.IsTorExitNode` — placeholder that always returns false

`Detectors/IpDetector.cs:207-212` — "`// This is a placeholder - in production, you'd maintain a
list of Tor exit nodes`" — always `return false`. The Tor exit-node branch (+0.5,
BotType.MaliciousBot) is unreachable. (File is dead code anyway per A1; the gateway's real Tor
path is `ThreatIntel/Providers/TorExitProvider`, subject to G6.)

---

## Atom-migration / commercial-seam artefacts (NOT bugs)

### A1 [LOW] Five legacy detectors are registered but never resolved — migration artefact, flag for deletion

`Detectors/UserAgentDetector.cs`, `Detectors/IpDetector.cs`, `Detectors/HeaderDetector.cs`,
`Detectors/InconsistencyDetector.cs`, `Detectors/SecurityToolDetector.cs` +
**`Modules/BotDetectionModule.cs:307-311` (their registrations)**

Their detection surfaces were **deliberately replaced by native atoms**, verified one-by-one:

| Dead detector | Replacement atom | Coverage confirmed |
|---|---|---|
| UserAgentDetector | `UserAgentAtom` (registered) | YAML-seeded UA classification; raises `user_agent.is_bot`, bot type/name (UserAgentAtom.cs:132-184) |
| IpDetector | `IpAtom` + TorExitProvider | IP classification, datacenter prefixes/ASN/CIDR, proxy topology; Tor via ThreatIntel TorExitProvider (IpDetector's own Tor branch was a false placeholder — G16) |
| HeaderDetector | `HeaderAtom` (registered) | sec-fetch attestation, missing-Accept-Language, programmatic-request signals (HeaderAtom.cs:88-117) |
| InconsistencyDetector | `InconsistencyAtom` (registered) | 9+ inconsistency contribution sites (InconsistencyAtom.cs:79-218) |
| SecurityToolDetector | `SecurityToolAtom` (registered) | Richer than the dead class: raises SecurityToolDetected/Name/Category + MaliciousBot (SecurityToolAtom.cs:117-124) |

**But the 5 classes are still registered in DI** (`BotDetectionModule.cs:307-311` — registered
alongside the 4 that atoms actually inject: HeuristicDetector→HeuristicAtom/HeuristicLateAtom,
VersionAgeDetector→VersionAgeAtom, BehavioralDetector→BehavioralAtom, ClientSideDetector→
ClientSideAtom). Nothing resolves them — they are DI-registered corpses: the container claims
they're active, they never run, and any edit to them (or to `BotSignatures`) looks live but
changes nothing.

**Deletion plan (route to stylobot-):** delete the 5 classes + registrations at
BotDetectionModule.cs:307-311. **Keep `Data/BotSignatures.cs`** — it is partially live:
`GoodBots` is populated from YAML by `BotPatternLoader` and `CompiledPatternCache`/
`BotListFetcher` reference it. Only the hardcoded `MaliciousBotPatterns` /
`AutomationFrameworks` / `BotPatterns` / `CompiledBotPatterns` members are referenced solely by
the dead `UserAgentDetector` — trim those after confirming no other reader (check
`CompiledPatternCache` usage first).

### A2 [LOW] Hardcoded word lists — YAML-migration debt (all carry `TODO: migrate to YAML per feedback_no_word_lists`)

Genuinely hardcoded (class 3), but each list is *within* a live atom and functions; the debt is
data-location, not broken detection:

- `IpAtom.cs:48-58` — datacenter prefix catalog (see G14)
- `TlsFingerprintAtom.cs:42-59` — fabricated JA3 sets (G7)
- `Http2FingerprintAtom.cs:34-67` — 30-entry SETTINGS fingerprint catalog (real data)
- `Http3FingerprintAtom.cs:37-53` — QUIC transport fingerprint dictionary
- `TcpIpFingerprintAtom.cs:37-94` — window/TTL pattern tables (plus the collision in L2)
- `TransportProtocolAtom.cs:36-41` — UA-family list (6 entries)
- `IntentAtom.cs:343-378` — probe/admin/auth path keyword catalogue (".env", "wp-admin",
  "phpmyadmin", ".git", "actuator", "phpinfo", "/admin", "/login", "/graphql", ...) + static-extension list
- `FediverseDomainAtom.cs:52` — 18-name fediverse software regex
  (Mastodon|Pleroma|Misskey|Akkoma|Firefish|GoToSocial|PeerTube|Lemmy|kbin|Friendica|...);
  new/lesser-known servers silently never reach NodeInfo verification
- `MultiLayerCorrelationAtom.cs:36-45` — 7-country country→language table; the
  geo-vs-Accept-Language correlation arm silently returns false for every other country
- `UserAgentAtom.cs:358` — `\b(bot|crawler|spider|scraper)\b` keyword regex
- `BrowserCharConsistencyAtom.cs:129-138` — UA-family→centroid-family mapping
- `CacheBehaviorAtom.cs:273-282`, `BehavioralWaveformAtom.cs:652-657`,
  `ResourceWaterfallAtom.cs:218-242`, `StreamAbuseAtom.cs:226-230` — static-asset extension lists
- `HeaderCorrelationAtom.cs:159-163` — 18-name volatile-header skip list
- `RequestHydratorAtom.cs:95-130` — duplicate UA keyword list (A3)
- `SignatureFeedbackHandler.cs:255-278` — keyword→UA-family classifier
- `CommonUserAgentService.cs:218,239,253` — hardcoded `useragents.me` URL + fragile CSS selectors
  with no config override (BrowserVersionService's URL is configurable — inconsistency)
- `AsnLookupService.cs:58-111` (~54 ASN→provider entries), `:197-201` (hosting keywords),
  `:272` (hardcoded `8.8.8.8:53`)

### A3 [LOW] `RequestHydratorAtom.DetectAsync` is a placeholder that still runs — migration artefact

`Orchestration/Atoms/RequestHydratorAtom.cs:29` + `BotDetectionOrchestrator.cs:93`

`DetectAsync` is `await Task.CompletedTask` + a canned Info; the real work lives in the static
`HydrateFromContext` which the orchestrator calls directly. The empty atom is still registered and
runs at Priority 0 on every request; its class doc falsely claims it "runs first and emits
canonical signals". Harmless (the hydration genuinely happens via the static path) but
misleading + per-request dead work. Also duplicates the UA keyword list (A2).

### A4 [LOW] `ICveFingerprintMatcher` defaults to a silent no-op — intentional commercial seam

`ThreatIntel/ICveFingerprintMatcher.cs:53-62` + `Modules/BotDetectionModule.cs:224`
(`TryAddSingleton<ICveFingerprintMatcher, NullCveFingerprintMatcher>()`)

**By design:** the commercial repo registers `CommercialCveFingerprintMatcher`
(`Stylobot.Commercial.GatewayPlugin/ServiceCollectionExtensions.cs:476`). FOSS's Null default is
the documented seam pattern. At most, log at startup that CVE matching is a no-op in FOSS.

### A5 [LOW] `IBotNameSynthesizer` unregistered in FOSS — intentional commercial seam

`Services/NoOpBotNameSynthesizer.cs` + `DeterministicBotNameSynthesizer.cs` (never registered);
the Llm pack registers `LlmBotNameSynthesizer` (`BotDetection.Llm/Extensions/LlmServiceExtensions.cs:25`).
Same seam pattern as A4. Optional improvement: register `DeterministicBotNameSynthesizer` in FOSS
so FOSS bots get stable names.

### A6 [LOW] `SqliteVecIdentityAnchorIndex` unreachable in FOSS — commercial seam

`Identity/SqliteVecIdentityAnchorIndex.cs` — vec0 fast path registered nowhere in FOSS
(`BruteForceIdentityAnchorIndex` is the default); the commercial Postgres pack supplies its own
vector index. Consistent with the seam pattern.

---

## LOW — genuine but minor

### L1. `TcpIpFingerprintAtom` window-size ambiguity flags legitimate Windows as bot

`TcpIpFingerprintAtom.cs:302-323` + `:37-72` — window size 65535 matches Windows _and_ MacOS
_and_ FreeBSD _and_ four "Bot" entries; the code unions ALL patterns for a size and flags bot if
**any** pattern says Bot → a standard Windows/macOS default window (65535) scores +0.55×1.3 bot
whenever the (currently never-injected, G1) X-TCP-Window header is present. Same collision at
8192/32768/16384/64240. Currently inert only because of G1 — must be fixed before any header lands.

### L2. Dead/no-op option surface

- `Models/BotDetectionOptions.cs:1751-1754` — `CollectAudio` "not implemented, reserved for future
  use", default false (wired into the tag helper + validation, so a non-default config silently
  collects nothing)
- `WatchdogDef.CheckPathCentroid` (`DetectionPolicyConfiguration.cs:413`) — "not implemented in
  v1" comment is stale; `VarianceWatchdog.cs:160` does read it. Comment debt only.
- `GeoProvider.IpApiCo` maps to `IpApiGeoLocationService` (`ServiceCollectionExtensions.cs:95`) —
  ip-api.com endpoint, not ipapi.co; selecting IpApiCo gets the wrong provider's limits and
  semantics. Unknown provider values silently fall to `Simple` (`:102`).

### L3. Dead methods / private stubs

- `Services/SignatureFeedbackHandler.cs:361-365` — `ComputeCombinedSignature` never called (tests only)
- `Services/BotClusterService.cs:1343-1346` — `GenerateLabel` wrapper never called in production
- `Services/DriftDetectionHandler.cs:108` — TODO: SQLite persistence; drift history lost on restart
- `Markov/PopulationMarkovService.cs:78` — snapshot tick updates `_lastSnapshotUtc` without
  snapshotting (TimescaleDB TODO)
- `MaxMindGeoLocationService.cs:33` — dead field `_anonymousReader`
- `Orchestration/Atoms/IntentAtom.cs:303` — `var driftMagnitude = 0f;` pins the
  `session:drift_magnitude` intent feature to constant 0 forever (dead dimension silently feeding
  the vectorizer)

### L4. `SecurityToolDetector` first-load failure leaves the detector inert for the refresh window

`Detectors/SecurityToolDetector.cs:147-151` — fetch failure returns cached-or-empty; with no
cache the detector stays empty until a later refresh succeeds. Logged at Warning (audible) but the
empty state is not surfaced as "patterns unavailable". (Dead file anyway per A1.)

### L5. `ProjectHoneypotAtom` swallow

`Orchestration/Atoms/ProjectHoneypotAtom.cs:144-146` — catch around the HTTP:BL lookup swallows
non-cancellation exceptions and returns None with only LogDebug; a persistent third-party outage
silently disables IP reputation at no warning level. (Compound with G12.)

---

## Verified clean (no findings)

- `NullFingerprintStore` — intentional ephemeral no-op, registered only via `Replace` in the
  in-memory test surface; FOSS default is `SqliteFingerprintStore`.
- `FingerprintNameComposer` — names derive from YAML bot-patterns catalog, no word lists.
- `SignatureConvergenceService`, `FingerprintDriftService`, `BotListUpdateService`,
  `IdentityProcessingCoordinator`, `ThreatIntelEnrichmentQueue` — failures logged, retry/backoff
  documented, thresholds config-driven.
- `UaProfileStore` — YAML-seeded.
- `HeuristicDetector` / `HeuristicFeatureExtractor` — real linear model; feature weights are
  tuned defaults (borderline, not a word list).
- `BehavioralDetector`, `ClientSideDetector`, `VersionAgeDetector`, `InconsistencyDetector`,
  `HeaderDetector` — functional logic, no hardcoded returns.
- Atom registration: all 66 atom classes are registered (65 via `AddNativeDetectorAtoms`,
  RequestHydratorAtom via line 282); no orphan atoms. The Identity-gated atoms
  (BrowserCharConsistency/BrowserModeClassifier/FingerprintMatch/IdentityVector) are documented
  dormancy when `Identity:Enabled=false`, not a bug.

---

## Summary

| Class | Genuine | Artefact |
|---|---|---|
| 1. Hardcoded false/true | G7, G16, G15, L3 IntentAtom drift=0 | — |
| 2. Dead placeholders | G8 AiAtom, A3 RequestHydratorAtom, L3×2 | — |
| 3. Hardcoded word lists | G14, G15 fediverse, A2 (17 sites) | — |
| 4. Unwired seams | G2, G3, G4, G5, G9, G10, G11, L2 IpApiCo | A4, A5, A6 |
| 5. Silent failures | G12, G13 simple-fallback, L4, L5 | — |
| 6. Never-reached code | G1 (delivery), G3, G5, G9, G10 | A1, A3 |

**Top fixes (highest leverage, smallest diffs):**
1. Register the unregistered seams — `IJa3ReferenceIndex`, `IAsnLookupService`, `IClusterStore`
   (FOSS's documented default!), `ResponseCoordinator`, `ISessionVectorSearch`,
   `IntentClassificationCoordinator`, `IBotClusterReader`/`IClusterMembershipLookup` (G2-G5,
   G9-G11 — each one line, each unlocks a documented detection layer).
2. Fix the transport-fingerprint delivery (G1) — route captured TLS metadata to the atoms; then
   re-verify the TcpIp window-size collisions (L1) before any header lands.
3. Gate ThreatIntel providers on their own `Enabled` flags (G6).
4. Replace fabricated JA3 sets with real corpus data (G7) — or source them from the corpus G2
   unlocks.
5. Delete the 5 dead detectors + their DI registrations (A1; BotDetectionModule.cs:307-311) so
   the codebase stops implying they run. Trim the hardcoded BotSignatures members only after
   confirming `CompiledPatternCache` doesn't read them.
6. Delete or rename the AiAtom placeholder evidence (G8).

---

## Cleanup-pass scope (routed to stylobot- / cicd- / drain-)

From the follow-up sweep (2026-08-04). This is a cleanup pass, not a feature change.

### Dead code — verified inventory (FOSS)

- 5 legacy detector classes + their registrations (A1): `UserAgentDetector`, `IpDetector`,
  `HeaderDetector`, `InconsistencyDetector`, `SecurityToolDetector` +
  `BotDetectionModule.cs:307-311`.
- Hardcoded members of `BotSignatures.cs` (`MaliciousBotPatterns`, `AutomationFrameworks`,
  `BotPatterns`, `CompiledBotPatterns`) — referenced only by the dead UserAgentDetector
  (verify `CompiledPatternCache` first).
- Methods never called in production (L3): `SignatureFeedbackHandler.ComputeCombinedSignature`,
  `BotClusterService.GenerateLabel` wrapper.
- `DriftDetectionHandler` (L3) — bounded in-memory only; SQLite persistence TODO.
- Full mechanical dead-code sweep (Roslyn/IDE analyzers over both solutions) recommended as a
  separate pass — this audit verified the known candidates, not exhaustively every private member.

### NuGet — outdated (FOSS `mostlylucid.stylobot.sln`)

Safe patch/minor bumps: `Microsoft.Extensions.*` 10.0.9→10.0.10 (many), `Microsoft.NET.Test.Sdk`
18.6.0→18.8.1, `Grpc.*` 2.80→2.83, `OpenTelemetry` 1.16.0→1.17.0 + Instrumentation.AspNetCore
1.15.2→1.17.0, `Serilog` 4.3.1→4.4.0, `OllamaSharp` 5.4.25→5.4.30, `Microsoft.AspNetCore.SignalR.
Client` 10.0.9→10.0.10, `Microsoft.AspNetCore.Mvc.Testing` 10.0.9→10.0.10.
**Major-version risk (check breaking changes before bumping):**
- `Microsoft.OpenApi` 2.7.5 → **3.9.0** (used by BotDetection.Api; new OpenApi.NET major)
- `Mostlylucid.StyloExtract.AspNetCore` 1.6.1 → **2.0.1**

### NuGet — vulnerabilities + restore blocker (commercial `Stylobot.Commercial.slnx`)

- **NU1903 HIGH:** `Scriban` 7.2.1 (GHSA-7jvp-hj45-2f2m) — ControlPlane, Reporting,
  IntegrationTests. `System.Security.Cryptography.Xml` 10.0.8 (5 advisories) — IntegrationTests.
  `Microsoft.OpenApi` 2.0.0 (GHSA-v5pm-xwqc-g5wc) — ControlPlane.
- **NU1902 MODERATE:** `OpenTelemetry.Api` 1.11.2 (GHSA-g94r-2vxg-569j) — 7 projects;
  `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.2 (2 advisories) — website, IntegrationTests.
- **NU1510:** unnecessary `Microsoft.Extensions.Configuration.Binder` (2 test projects) and
  Hosting/Logging/Options/DI.Abstractions (Cluster.Mesh) — prune.
- **Restore blocker:** user-level `~/.nuget/NuGet/NuGet.Config` has stale sources `local-dev`
  (`/tmp/local-nuget`, missing) and `cve-local` (`/tmp/cve-pack`) — GatewayHost restore fails
  with NU1301. Remove the two local entries.

### Zero build warnings

- FOSS `Mostlylucid.BotDetection` baseline: **179 warnings** — overwhelmingly mechanical
  CA1822 (can-be-static), CA1860 (Any() vs Count), CA1850 (SHA256.HashData), CA1854
  (TryGetValue). Safe to fix en masse; full solution baseline not yet measured.
- Treat warnings as errors (`TreatWarningsAsErrors` + `WarningsNotAsErrors` for justified
  exclusions) after the mechanical pass lands.
