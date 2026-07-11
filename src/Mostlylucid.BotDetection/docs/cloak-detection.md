# Cloak-Browser Detection

This document covers the detection probes, supporting infrastructure, and configuration knobs added to address the modern anti-detect / cloak-browser ecosystem. These tools (damru, Bright Data Scraping Browser, Multilogin Mimic, Kameleo Chroma, Bright Data's WebView-based mobile emulator, and vanilla Puppeteer/Playwright) defeat the older browser-marker checks by patching the Chromium fork below the JS surface. The probes below target axes those patches cannot easily forge: kernel-level network behaviour, hardware-derived TLS handshakes, OS-managed media subsystems, and per-pixel input synthesis.

## Detection map

| Cloak target | Primary catch | Backup catch |
|---|---|---|
| damru (Redroid/Android-emulator Playwright) | `clientside.connection_type` mismatch (Android UA + ethernet/wifi) | `clientside.ice_no_srflx` (iptables blocks Chrome UID), `clientside.tts_voice_count == 0`, `tls.cipher_subset_of_real_chrome` |
| Bright Data Scraping Browser | `clientside.ice_no_srflx` (restricted egress in hosted env), BotD `headless_chrome` kind | `clientside.cdp_runtime` console.debug counter |
| Multilogin Mimic | `tls.version_delta_from_ua` (patched Chromium TLS lags stable), `risk.shape_hash_changed` (profile swap) | `clientside.pool_collision_contexts` (same shape across many IPs) |
| Kameleo Chroma | `clientside.mouse_all_integer_coords` (CDP-synthesised mousemoves) | `clientside.mouse_timing_cv` (synthesised intervals), `tls.version_delta_from_ua` |
| Vanilla Puppeteer / Playwright | BotD `puppeteer`/`playwright` kind | `clientside.cdp_runtime`, existing webdriver probes |
| Selenium | BotD `selenium` kind | existing webdriver probes |

The list is illustrative, not exhaustive. The probes write privacy-safe signals; the heuristic model and the identity-drift layer compose them into the final verdict.

---

## Client-side probes

All client-side probes are added to `src/Mostlylucid.BotDetection/ClientSide/botdetection.js`. Their results are deserialised into nested blocks on `BrowserFingerprintResult` (see "DTO shape" below), turned into signals by `ClientSide.BrowserFingerprintAnalyzer`, and consumed by `Orchestration.ContributingDetectors.InconsistencyAtom` plus the heuristic feature extractor.

### `connType` (Plan 1: mobile + connection-type mismatch)

**Targets:** damru and other Android-emulator stacks. Real Android Chrome on a phone reports `cellular` or `wifi` from `navigator.connection.type`. Redroid containers running on a host with ethernet uplink report `ethernet` because `Network.overrideNetworkState` was skipped during the headless-Chromium patch.

**Mechanism:** the JS probe records `navigator.connection?.type` (string, or empty when the Network Information API is unavailable). The analyzer writes it to `SignalKeys.ClientSideConnectionType` ("clientside.connection_type"). `InconsistencyAtom` triggers a `mobile_connection_mismatch` flag when the UA contains a mobile token and the connection type is `ethernet`, `wifi`, or `mixed`.

**Signal:** `SignalKeys.ClientSideConnectionType` (string).

**Heuristic feature:** `sigv:conn_type:{value}` (string-enum, 1f when present).

### `iceProbe()` (Plan 3a: WebRTC ICE no-srflx)

**Targets:** damru (iptables drops Chrome's UID at the OS layer), Bright Data Scraping Browser (restricted egress in their hosted environment), locked-down corporate VMs.

**Mechanism:** the probe creates an `RTCPeerConnection` with a single STUN server (`ICE_STUN_URL`), calls `createOffer()`, and waits 2000ms for ICE gathering to produce candidates. A real device on a real network produces at least one `srflx` (server-reflexive) candidate because the STUN reply traverses the NAT. Devices with UDP egress blocked never see `srflx`. The probe records: gathering completion (bool), srflx count (int), host/relay counts (ints).

The STUN server URL is configurable via `ClientSideOptions.IceStunServerUrl` (default Google's public STUN); set to empty string to disable the probe entirely. Privacy-sensitive deployments should self-host coturn inside their compliance boundary.

**Signal:** `SignalKeys.ClientSideIceNoSrflx` (bool: true when the probe completed and no srflx appeared).

**Consumed by:** `InconsistencyAtom` gates the check on mobile UA-CH because some desktop captive portals legitimately drop UDP. Heuristic feature: `sigv:ice_no_srflx` (bool, 1f).

### `ttsProbe()` (Plan 3b: TTS voice list)

**Targets:** Android-emulator stacks. Real Android Chrome populates `speechSynthesis.getVoices()` before any script runs because the TTS engine starts at boot. Fresh Redroid containers start with an empty list until the first user gesture; damru spins a container per session.

**Mechanism:** call `getVoices()` immediately, then wait up to 200ms for `voiceschanged` to populate the list. Record the final count (int).

**Signal:** `SignalKeys.ClientSideTtsVoiceCount` (int).

**Consumed by:** `InconsistencyAtom` triggers `android_empty_voices` only when the UA contains "Android" and the count is zero. iOS Safari has a different voice lifecycle so the check is Android-only by design. Heuristic feature: `sigv:tts_voice_count` (normalised /20).

### `cdpRuntime` (Plan 3c: CDP Runtime detection)

**Targets:** vanilla Puppeteer / Playwright, Bright Data Scraping Browser, anything driven by Chrome DevTools Protocol.

**Mechanism:** the Runtime domain hooks `console.debug` to fire a stringification side-effect. Reading `console.debug.toString()` from a CDP-driven page returns a different string than from a vanilla page, and the act of reading it can be counted. The probe records both signals on `basics().cdpRuntime`.

**Signal:** lands as part of the basics block; consumed alongside the existing webdriver/headless probes.

### `botdProbe()` (BotD integration)

**Targets:** the long tail BotD already covers: Selenium, PhantomJS, CefSharp, Awesomium, Nightmare, plus 40+ distinctive-property fingerprints.

**Mechanism:** when `ClientSideOptions.Botd.Enabled` is true, the script dynamic-imports BotD from `ClientSideOptions.Botd.ScriptUrl` (default `https://openfpcdn.io/botd/v2`; self-hosted recommendation is to vendor `wwwroot/lib/botd.min.js` and avoid the third-party CSP origin). The integration always calls BotD with `monitoring: false` to suppress their 0.1%-sampled telemetry.

**Signal:** `SignalKeys.ClientSideBotdKind` (string: "selenium", "puppeteer", "headless_chrome", etc.; null when BotD did not classify).

**Heuristic feature:** `sigv:botd_kind:{value}` (string-enum, 1f when present).

**Why BotD over a homegrown marker dictionary:** BotD is MIT-licensed, covers automation frameworks that StyloBot does not currently chase, and stays maintained by FingerprintJS. StyloBot's probes target the modern cloak ecosystem (damru, Bright Data, Multilogin, Kameleo) that BotD does not address; the two layers compose without overlap.

### `mouseStats()` (Bonus B: Kameleo Chroma synthesised mouse)

**Targets:** Kameleo Chroma's CDP-synthesised mousemoves. Real mice produce sub-pixel float coordinates on any DPR > 1; Kameleo emits integer coordinates via the CDP `Input.dispatchMouseEvent` path. Real human mouse intervals have a coefficient of variation (stddev / mean of inter-event delta) above 0.5; synthesised intervals are nearly constant.

**Mechanism:** an event listener samples up to 50 mousemove events. Per sample it records `clientX`, `clientY`, and timestamp. From those it computes: total count, integer-coords-only flag, mean delta, stddev delta, and CV.

**Signals:**
- `SignalKeys.ClientMouseEvents` (int: sample count; also lights up the previously-orphaned ghost signal that `BehavioralWaveformAtom` already consumed)
- `SignalKeys.ClientSideMouseAllIntegerCoords` (bool)
- `SignalKeys.ClientSideMouseTimingCv` (double)

**Consumed by:** `InconsistencyAtom` triggers `kameleo_mouse_synthesis` when the UA is desktop, the sample count is non-trivial, and either the integer-only flag is true or the CV is below 0.5. Heuristic features: `sigv:mouse_events` (normalised /50), `sigv:mouse_all_integer` (bool, 1f), `sigv:mouse_timing_cv` (double).

### `shape_hash` (Bonus A: fingerprint shape hash)

**Targets:** Multilogin Mimic and Kameleo Chroma profile rotation. Both products cycle curated profiles per session; the canvas + WebGL renderer triple is the load-bearing identity inside each profile and stays stable per profile. A bot operator running N profiles across M IPs leaves a footprint of "same shape under many distinct contexts".

**Mechanism:** `BrowserFingerprintAnalyzer.GenerateShapeHash()` computes `xxHash64(canvas | vendor | renderer)`. The 16-char hex string is written to `SignalKeys.ClientSideShapeHash`. The `PoolCollisionAtom` looks it up in `IFingerprintPoolCollisionTracker` (SQLite-backed; see "Pool collision store" below), records the current (IP-hash, session-id) context, and writes the distinct-context count to `SignalKeys.ClientSidePoolCollisionContexts`. Above the configured threshold (default 3 contexts within a 6h window) the contributor emits a bot vote.

**Why the canvas+WebGL triple:** it is hardware-derived (GPU, driver, OS) and effectively immutable for a real user across sessions on the same device. A change under the same fingerprint id is the canonical anti-detect-browser profile-swap signal.

**Signals:**
- `SignalKeys.ClientSideShapeHash` (string: 16-hex xxHash64)
- `SignalKeys.ClientSidePoolCollisionContexts` (int)

**Heuristic features:** `sigv:shape_hash_present` (presence-only flag, 1f when the hash exists; deliberately *not* the hash itself to avoid cardinality explosion in the feature space), `sigv:pool_collision_contexts` (normalised /5).

---

## TLS corpus probes

The JA3 reference corpus lives in `src/Mostlylucid.BotDetection/Definitions/TlsReference/tls-reference-corpus.yaml` and is embedded into the assembly. `Ja3ReferenceIndex` loads it at startup. Two checks consume it.

### Cipher-list subset (Plan 2a)

**Targets:** the entire damru family (~184 cipher-list variants per Cloudflare's published catalogue). Damru and similar tools blacklist specific cipher suites from the real Chrome list to evade fingerprint matchers; the patched ClientHello is otherwise byte-identical to real Chrome.

**Mechanism:** `TlsFingerprintAtom` parses the observed JA3 string into its five parts (TLS version, ciphers, extensions, elliptic curves, EC point formats). It looks up the reference JA3 for the UA-claimed browser + major version + mobile/desktop class via `IJa3ReferenceIndex.GetReference()`. `IsStrictCipherSubset()` returns true when parts 0/2/3/4 match exactly and the cipher list is a strict subset (every observed cipher is present in the reference, and at least one reference cipher is missing). Subset + missing count are written as signals.

**Signals:**
- `SignalKeys.TlsCipherSubsetOfRealChrome` (bool)
- `SignalKeys.TlsCipherSubsetMissingCount` (int: damru ships up to 3 missing per profile rotation)

**Heuristic features:** `sigv:tls_cipher_subset` (bool, 1f), `sigv:tls_cipher_subset_missing` (normalised /5).

### Version delta from UA (Plan 2a)

**Targets:** Multilogin Mimic, Kameleo Chroma, and any patched Chromium fork whose TLS lags Chrome stable by one or two releases.

**Mechanism:** `ParseUaBrowserClaim()` extracts the claimed browser major version from the UA (handling Edge/Opera token order so they aren't classified as Chrome). `MatchAnyVersion()` finds which corpus entry the observed JA3 matches. The delta (claim minus matched) is written to the signal.

**Signal:** `SignalKeys.TlsVersionDeltaFromUa` (int, positive when UA claims newer than the JA3 supports).

**Heuristic feature:** `sigv:tls_version_delta` (normalised /10).

### Signed corpus refresh service (Plan 2b)

Embedded corpora go stale as new browser versions ship. `Ja3CorpusRefreshService` is an opt-in `BackgroundService` that periodically downloads an Ed25519-signed corpus envelope and atomically replaces the in-memory `IJa3ReferenceIndex`.

**Wire-up:** `BotDetectionOptions.TlsCorpus` (a `TlsCorpusOptions`). Enabled = false by default; the embedded baseline corpus is the only source until this is turned on.

**Required configuration when enabled:**
- `RefreshUrl` (HTTPS): the corpus envelope endpoint; service refuses to start with an empty URL.
- `PublicKey` (base64 raw 32-byte Ed25519) **or** the `STYLOBOT_TLS_CORPUS_PUBLIC_KEY` env-var override; service refuses to start when neither is set.

**Defensive defaults:**
- `RefreshInterval` 6 hours, floor 5 minutes enforced to prevent a misconfigured value from hammering a public mirror.
- `MaxEnvelopeBytes` 256 KiB hard cap on the downloaded body (`HttpClient.MaxResponseContentBufferSize`). Prevents a malicious mirror from feeding a multi-GB payload that exhausts memory before the verifier gets a chance to reject it.
- Verify-before-parse: the signature is checked against the raw YAML body before YamlDotNet sees it, so any YAML parser bug is unreachable via a forged envelope.

**Why Ed25519 (NSec.Cryptography):** small (64-byte) signatures, deterministic verification, no key-derivation surface; well-suited to a "vendor signs, customer verifies" distribution model. The public key is not a secret and is deliberately *not* marked `[Secret]`: operators auditing "is the refresh wired to the right key?" need to see it.

---

## Identity-drift integration

The metastable fingerprint identity layer (6.4.7+) compares the current request's `FingerprintDimSnapshot` against the matched fingerprint's prior observations and flags dimension-level drift. Two new dims were added this session.

**`shape_hash` dim (weight 0.40, highest single-dim weight):** a change in the hardware-derived canvas+WebGL triple under the same fingerprint id is the canonical anti-detect-browser profile swap. Higher weight than the existing country / UA-family / infra dims because the underlying signal is harder to forge.

**`botd_kind` dim (weight 0.20):** a fingerprint that BotD classified as `selenium` last session and `puppeteer` this session either swapped automation framework (rare for legitimate operators) or is being reused across accounts.

`IdentityChangeAtom` reads `ClientSideShapeHash` and `ClientSideBotdKind` from the current signals, compares against the snapshot, and writes:

- `SignalKeys.RiskShapeHashChanged` (bool)
- `SignalKeys.RiskBotdKindChanged` (bool)
- `SignalKeys.RiskSuspiciousChangeScore` (double, weighted aggregate)
- `SignalKeys.RiskSuspiciousChangeReason` (string, human-readable summary; drives dashboard messaging)

The aggregate score stays well under 1.0 even when "everything changed" because the FOSS layer is informational; commercial layers thresholds and alerting on top.

**Heuristic features:** `sigv:risk_country_changed`, `sigv:risk_asn_changed`, `sigv:risk_ua_family_changed`, `sigv:risk_infra_changed`, `sigv:risk_shape_hash_changed`, `sigv:risk_botd_kind_changed` (all bool, 1f when set), and `sigv:risk_suspicious_change_score` (double).

---

## Storage infrastructure

Two new SQLite-backed stores were added. Both subclass `WriteBehindLfuStore<TKey, TValue, TWriteOp>` (the canonical persistence pattern: hot `ConcurrentDictionary` tier + bounded write queue + background drainer + LFU eviction + SQLite cold read-through). This is the only correct pattern for adding new persistent state inside the detection pipeline; rolling a parallel `ConcurrentDictionary + linear-scan LRU` is architectural drift.

### `SqlitePoolCollisionStore`

**File:** `src/Mostlylucid.BotDetection/Identity/SqlitePoolCollisionStore.cs`
**DB:** `pool_collisions.db`
**Schema:** `(shape_hash, context_key, last_seen_ticks)` with `(shape_hash, context_key)` PRIMARY KEY.
**Cold-load window:** 6h default; older entries fall out of the warm hot-tier and the cold read-through only returns within-window observations.
**Merge semantics:** `MergeIntoExisting` produces a new `ImmutableDictionary<string, long>` (immutable record pattern); concurrent contributors never share a mutable bucket.

`PoolCollisionInitService` kicks off the cold-load on startup.

### `WaveformHistoryStore`

**File:** `src/Mostlylucid.BotDetection/Orchestration/Atoms/WaveformHistoryStore.cs`
**DB:** `waveform_history.db`
**Purpose:** replaces the previous `IMemoryCache` usage in `BehavioralWaveformAtom` (per the "no unbacked IMemoryCache" rule; new IMemoryCache without a backing store is a bug).
**Privacy:** hashes UA via xxHash64 before SQLite persistence; raw UA stays in the hot tier only.
**Window:** 30-minute sliding window + 100-snapshot cap per signature.
**In-place update:** `UpdateLastContentClass` uses same-timestamp merge so the latest snapshot updates without an insert/delete cycle.

`WaveformHistoryInitService` kicks off the cold-load on startup.

---

## Heuristic feature wiring

`HeuristicFeatureExtractor.ExtractStructuredSignalValues` was extended this session with 17 new signal-to-feature mappings, covering everything the probes write. Without these, the signal exists and the contributor writes it but the heuristic model never sees it (the original ghost-signal failure mode that prompted the audit).

| Signal | Feature key | Encoding |
|---|---|---|
| `RiskCountryChanged` | `sigv:risk_country_changed` | bool → 1f |
| `RiskAsnChanged` | `sigv:risk_asn_changed` | bool → 1f |
| `RiskUaFamilyChanged` | `sigv:risk_ua_family_changed` | bool → 1f |
| `RiskInfraChanged` | `sigv:risk_infra_changed` | bool → 1f |
| `RiskShapeHashChanged` | `sigv:risk_shape_hash_changed` | bool → 1f |
| `RiskBotdKindChanged` | `sigv:risk_botd_kind_changed` | bool → 1f |
| `RiskSuspiciousChangeScore` | `sigv:risk_suspicious_change_score` | double |
| `ClientSideIceNoSrflx` | `sigv:ice_no_srflx` | bool → 1f |
| `ClientSideMouseAllIntegerCoords` | `sigv:mouse_all_integer` | bool → 1f |
| `ClientSideMouseTimingCv` | `sigv:mouse_timing_cv` | double |
| `ClientMouseEvents` | `sigv:mouse_events` | normalised /50 |
| `ClientSideTtsVoiceCount` | `sigv:tts_voice_count` | normalised /20 |
| `ClientSidePoolCollisionContexts` | `sigv:pool_collision_contexts` | normalised /5 |
| `ClientSideBotdKind` | `sigv:botd_kind:{value}` | string-enum (1f for the present value) |
| `ClientSideConnectionType` | `sigv:conn_type:{value}` | string-enum |
| `ClientSideShapeHash` | `sigv:shape_hash_present` | presence-only flag (1f if non-empty); the hash itself is *not* a feature, to avoid cardinality explosion |
| `TlsCipherSubsetOfRealChrome` | `sigv:tls_cipher_subset` | bool → 1f |
| `TlsCipherSubsetMissingCount` | `sigv:tls_cipher_subset_missing` | normalised /5 |
| `TlsVersionDeltaFromUa` | `sigv:tls_version_delta` | normalised /10 |

Pinning tests live at `src/Mostlylucid.BotDetection.Test/Detectors/HeuristicFeatureExtractorSessionSignalsTests.cs`. The defensive `NoSignalsAtAll_ProducesNoSessionFeatureKeys` test catches a default-emit regression that would confuse "absence" with "zero" and lie to the model.

---

## DTO shape: `BrowserFingerprintData` nested blocks

The previous flat DTO silently dropped every nested signal arriving from the JS payload (System.Text.Json does not auto-flatten). Restructuring the DTO with nested blocks matching the JS payload (`BasicsBlock`, `TailBlock`, `HeadlessBlock`, `TouchBlock`, `StackBlock`, `TripleBlock`, `UaBlock`, `LegitBlock`, `ClampBlock`, `WebglBlock`, `IceProbeBlock`, `TtsProbeBlock`, `BotdBlock`, `MouseStatsBlock`) fixed the production bug. Picked this over a `JsonConverter` workaround because the nested shape is the correct long-term contract.

Two defensive details worth knowing:
- Sentinel `-1` denotes "the probe errored" (vs `0` meaning "the probe ran and observed nothing"). The analyzer respects the distinction when promoting to signals.
- `data.Error` is truncated to 200 chars before any log / persistence path to prevent a malicious payload from inflating storage with a multi-megabyte error string.

---

## Security: `[Secret]` attribute

The dashboard config serializer needs to redact API keys, bypass tokens, and signing material before rendering. The legacy approach was a regex against property names ending in `key` / `password` / `token` / `secret`. The regex missed `ApiBypassKeys` (plural) and `SecurityToolsOptions.ApiKeys`. The fix is `[Secret]`:

**File:** `src/Mostlylucid.BotDetection/Models/SecretAttribute.cs`

```csharp
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class SecretAttribute : Attribute { }
```

Applied to:
- `BotDetectionOptions.ApiBypassKeys` (List\<string\>)
- `TrainingEndpointsOptions.ApiKeys`
- `BdfReplayOptions.ApiKeys`
- All scalar API-key / signing-secret properties.

`EffectiveConfigSerializer` (in `Mostlylucid.BotDetection.UI/Services/`) handles three masking shapes:
- Scalar (string, byte[], Guid) → `"***"`
- `List<string>` → same-length list of `"***"`
- `Dictionary<string,string>` → same-key dict with `"***"` values

`MaskingConverter` writes the same-shape redaction so callers can still see the *structure* (count, key names) without leaking the *values*. The plural-suffix regex (`(?i)(secret|password|token|key)s?$`) is retained as a defence-in-depth fallback, but `[Secret]` is the canonical declaration.

**Why a marker over the name regex:** explicit intent, survives field renames and type changes, catches collections that the name-only check missed. A marker also gives the auditor a single grep target (`rg "\[Secret\]"`).

**What is *not* marked:** `TlsCorpusOptions.PublicKey` (Ed25519 verification key). Public keys are by definition safe to publish; hiding it from operators auditing "is corpus refresh wired to the right key?" defeats the purpose. The corresponding private key lives in the build/release pipeline only.

---

## BDF replay visibility

`BdfReplayEndpoints` exposes the per-request signal probe list the dashboard renders. All probes added this session are surfaced via that endpoint so operators can verify which probes fired during a replay:
- `clientside.connection_type`, `clientside.ice_no_srflx`, `clientside.tts_voice_count`
- `clientside.botd_kind`, `clientside.shape_hash`, `clientside.pool_collision_contexts`
- `clientside.mouse_all_integer_coords`, `clientside.mouse_timing_cv`, `clientside.mouse_events`
- `tls.cipher_subset_of_real_chrome`, `tls.cipher_subset_missing_count`, `tls.version_delta_from_ua`
- `risk.shape_hash_changed`, `risk.botd_kind_changed`, `risk.suspicious_change_score`

The BDF replay rig at `src/Mostlylucid.BotDetection.Orchestration.Tests/Integration/BdfReplayTests.Integration.cs` runs the foundation contributors under `DetectionPolicy.Default` and asserts on the read surface. When adding a new probe, add a BDF assertion for the signal it writes; otherwise a future detector reorder can silently break the contract.

A known limitation: clean synthetic cloak scenarios score around 0.07 in the rig because other detectors vote human and dilute the cloak signals. The follow-up is a new BDF assertion shape that probes the new signals directly rather than asserting `isBot >= 0.5`.

---

## Configuration reference

All cloak-detection options live under `BotDetection` in `appsettings.json`. Default behaviour ships with most probes on; the two opt-ins are BotD and the corpus refresh service.

```json
{
  "BotDetection": {
    "ClientSide": {
      "IceStunServerUrl": "stun:stun.l.google.com:19302",
      "Botd": {
        "Enabled": false,
        "ScriptUrl": "https://openfpcdn.io/botd/v2"
      }
    },
    "TlsCorpus": {
      "Enabled": false,
      "RefreshUrl": "",
      "PublicKey": "",
      "RefreshInterval": "06:00:00",
      "MaxEnvelopeBytes": 262144
    }
  }
}
```

**`ClientSide.IceStunServerUrl`:** override for the WebRTC ICE probe's STUN server. Default Google's public STUN. Privacy-sensitive deployments should point this at self-hosted coturn inside their compliance boundary. Empty string disables the probe.

**`ClientSide.Botd.Enabled`:** off by default so existing deployments do not start a cross-origin fetch unexpectedly. When on, the client-side script dynamic-imports BotD and includes the verdict in the beacon.

**`ClientSide.Botd.ScriptUrl`:** the URL the client-side script `import()`s. Self-hosting recommendation: vendor the bundle into `wwwroot/lib/botd.min.js` and set this to `/lib/botd.min.js` so no third-party origin appears in your CSP.

**`TlsCorpus.Enabled`:** off by default. When on, `Ja3CorpusRefreshService` registers as a `BackgroundService` and the embedded baseline corpus is augmented (then atomically replaced) by refreshed entries.

**`TlsCorpus.RefreshUrl`:** HTTPS URL of the signed envelope. Required; service refuses to start without it.

**`TlsCorpus.PublicKey`:** base64-encoded raw 32-byte Ed25519 public key. Either this or the `STYLOBOT_TLS_CORPUS_PUBLIC_KEY` env-var override is required. Not marked `[Secret]` (public keys should be visible to auditors).

**`TlsCorpus.RefreshInterval`:** how often to fetch. Default 6h; minimum 5 minutes enforced.

**`TlsCorpus.MaxEnvelopeBytes`:** download size cap. Default 256 KiB.

---

## Known follow-ups

- **X-JA3 / X-JA4 / X-Client-TLS-* trusted-proxy gate.** The current `TlsFingerprintAtom` trusts these headers when HTTPS terminates upstream, but does not gate by a trusted-proxy CIDR list. An off-net attacker who can reach the origin directly can forge them. Tracked as security debt; the proper fix is a `BotDetectionOptions.TrustedProxyCidrs` list and a check before consuming any `X-*` recovery headers. The naive "trust on HTTPS" extension I committed in 5ef663f8 was reverted in 0fc3ad9a.
- **BDF cloak assertion shape.** New assertion API that asserts on the specific signals listed above, not on `isBot >= 0.5`, so the rig surfaces cloak-detection regressions independently of the global verdict.
- **CAPTCHA migration to Altcha.** Research complete; no code yet. The current PoW challenge stays in place.
