# StyloBot coverage audit vs. web-scraping-guide.com

**Date:** 2026-06-13
**Source reviewed:** https://web-scraping-guide.com/ ("Web Scraping 2026", single-page guide; sections Flow / Detect / Anti-Bots / Cases / Libs / Innov)
**Method:** Every bypass / anti-detection technique the guide describes was extracted, then mapped against StyloBot detection by reading `src/` directly (not docs, not CLAUDE.md). Five read-only sweeps covered transport/TLS, client-side JS, network identity, behaviour/session/identity, and UA/challenge layers. Coverage verdicts were verified against code; conflicts between sweeps were re-checked at the source line.

## How to read "coverage"

StyloBot is probabilistic and behavioural, not a signature checklist. A technique is **Covered** only when a *residual* signal survives even a perfect spoof and a detector actually consumes it. A flawless canvas hash is irrelevant if the session's request sequence, timing rhythm, or fingerprint-identity velocity still flags it; conversely, a "detector exists" is not coverage if the signal it needs never arrives.

Verdicts:
- **Covered** : residual signal exists and a detector consumes it.
- **Partial** : signal exists but is gated, weakly weighted, trust-spoofable, or only fires under conditions the bypass avoids.
- **Gap** : no residual signal StyloBot currently consumes.
- **N/A** : not StyloBot's layer (e.g. CAPTCHA-solving economics).

## The two structural findings first

Two things shape every row below; they matter more than any single detector.

1. **The richest layer (client-side JS) is absent for the guide's #1 recommended path.** StyloBot ships a real browser probe (`ClientSide/botdetection.js`) that collects canvas, WebGL, AudioContext, CDP traps, WebRTC ICE, permissions, timing-clamp residue, and mouse distribution, and scores them server-side via `ClientSideContributor`. This is genuinely strong. But it only fires if the client executes the probe. The guide's entire production priority order leads with **mobile API → XHR endpoint → JSON-in-HTML → HTTP scraping (curl_cffi/Scrapy)** before any browser is launched. An HTTP-only client never loads the page, never runs the probe, and the whole JS layer evaluates to nothing. For that traffic, coverage collapses to TLS + headers + IP + server-side behaviour. This is not a missing detector; it is a structural dependency, and it is the single most important thing to internalise from this audit.

2. **Transport fingerprint headers are trusted without proxy gating.** `TlsFingerprintContributor`, `Http2FingerprintContributor`, `Http3FingerprintContributor`, and `TcpIpFingerprintContributor` all read edge-injected headers (`X-JA3-Hash`/`X-JA3-String`, `X-Client-TLS-*`, `X-HTTP2-Settings`, `X-QUIC-*`, `X-TCP-*`) with **no trusted-proxy allowlist**. A client reaching the origin directly over HTTPS can set `X-JA3-Hash` to a known-Chrome value and earn the human bias (`known_browser_fingerprint_confidence: -0.15`), plus matching H2/QUIC/TCP human bonuses, with zero residual evidence of the spoof. This was previously noted for X-JA3 alone (see memory `project_x_ja3_header_trust_unverified`); the sweep confirms it spans all four transport detectors. It is an *inbound spoof of a human signal*, which is the most damaging direction. Production is documented as edge-fronted (Caddy/Cloudflare terminate TLS and inject these), but the code does not enforce that the request actually came from the edge.

## Coverage matrix

### Layer 1: TLS / transport fingerprinting

| Technique (guide) | StyloBot signal / detector | Verdict | Notes |
|---|---|---|---|
| curl_cffi / tls-client / utls Chrome-JA4 emulation | `TlsFingerprintContributor`: JA3/JA4, cipher-subset (damru), UA-vs-TLS version delta | **Partial** | Cipher-subset and version-delta checks only run when the reference corpus is loaded (`_referenceIndex` non-empty); otherwise silently skipped. A clean Chrome-JA4 emulation from a residential IP leaves little residual when the corpus is absent. |
| JA3/JA4 spoof via forwarded header | same, reading `X-JA3-*` | **Gap** | No proxy gating (finding #2). Spoofable human bias on direct HTTPS. |
| HTTP/2 frame / SETTINGS / pseudo-header-order spoofing | `Http2FingerprintContributor` + `MultiLayerCorrelation` UA-vs-H2 mismatch | **Partial** | Header-derived and spoofable in isolation, but `MultiLayerCorrelation` catches UA-claims-Chrome vs H2-says-Go (`browser_mismatch_confidence: 0.7`, weight 1.8). Consistent multi-layer spoof passes. |
| HTTP/3 / QUIC transport-param fingerprint | `Http3FingerprintContributor` (`X-QUIC-*`) | **Partial** | All QUIC params header-sourced; 0-RTT / migration human bonuses are spoofable. |
| TCP/IP p0f (window, TTL, options) | `TcpIpFingerprintContributor` (`X-TCP-*`) | **Partial** | Kestrel cannot see raw TCP; entirely header-dependent and spoofable. Real coverage only behind a proxy that computes these honestly. |
| UA vs TLS-stack mismatch | `InconsistencyDetector`, `MultiLayerCorrelation` | **Covered** | Strong when at least one transport layer is honest; perfect-consistency spoof earns `-0.25` human delta. |

### Layer 2: JavaScript interrogation (only when the probe executes; see finding #1)

| Technique (guide) | StyloBot signal / detector | Verdict | Notes |
|---|---|---|---|
| Canvas fingerprinting | `botdetection.js` canvas hash → `ClientSideContributor` / `BrowserFingerprintAnalyzer` | **Covered\*** | Hash only; Brave/Firefox-RFP/Tor noise suppressed via legitimacy markers. |
| WebGL vendor/renderer | shape hash (canvas+webgl) for Multilogin/Kameleo pool collision | **Covered\*** | |
| AudioContext FFT | audio hash | **Covered\*** | |
| CDP automation artifacts | two traps: `console.debug` getter + `toString()` call-counter | **Covered\*** | More robust than `navigator.webdriver` flag checks (see Innov: CDP-patch removal). |
| WebRTC IP leak / no-srflx | `iceProbe` → `clientside.ice_no_srflx` (mobile UA without UDP egress) | **Covered\*** | Catches the damru / datacenter-egress tell. |
| Function.toString() probing | error-stack frame analysis (`hasObjectApply`, `hasAnonymous`) | **Partial\*** | Detects injected wrappers, not native-function introspection. |
| WASM SIMD CPU fingerprint | none | **Gap** | Guide flags this as un-spoofable by Camoufox/CloakBrowser; a strong differentiating signal StyloBot does not collect. |
| IndexedDB iteration-order leak (CVE-2026-6770) | none | **Gap** | Not probed. |
| SharedArrayBuffer high-precision timers | none (related: `performance.now()` clamp residue *is* probed) | **Partial** | Clamp-residue engine fingerprint exists; the 17x-finer SAB timer is not used. |
| Chrome-extension URL probing (60 URLs) | none | **Gap** | Not probed; additive signal only. |
| Hyphenation-dictionary presence | none | **Gap** | Custom-Chromium-fork tell; not probed. |
| Headless markers (plugins, languages, chrome.runtime, Notification.permission) | `botdetection.js` headless markers + analyzer scoring | **Covered\*** | |

\* Covered only when the JS probe runs. Zero coverage for HTTP-only clients (finding #1).

### Layer 3: Network identity

| Technique (guide) | StyloBot signal / detector | Verdict | Notes |
|---|---|---|---|
| Residential / ISP proxy | `IpContributor` (Team Cymru ASN authoritative over prefix) | **Partial** | Clean residential IP earns `-0.15` human bias; the datacenter signal does not fire. Behaviour/reputation must carry detection. |
| Mobile-carrier IP | `IpContributor` | **Partial** | Same; mobile-carrier ASN reads as non-datacenter. |
| Datacenter-IP avoidance | `IpContributor` + `MultiLayerCorrelation` datacenter+browser | **Covered** (for datacenter) | Defeated by simply not using datacenter IPs. |
| AWS API Gateway IP rotation | `FastPathReputation` / `ReputationBias` (/24 + UA patterns) | **Partial** | Fresh /24 misses reputation cache; UA + combined signature still accrue, but continuous rotation keeps patterns juvenile (ConfirmedBad needs support >=50 over 3-12h decay). |
| Geographic-coherence spoofing | `GeoContributor` + `MultiLayerCorrelation` geo-vs-language, bot-origin | **Partial** | Single-vector (IP-geo + Accept-Language + known-bot-origin). The guide describes 5-vector alignment; StyloBot has no TLS-locale / client-timezone / DNS-resolver binding. Language-country map covers ~7 countries. |
| Project Honeypot DNSBL | `ProjectHoneypotContributor` | **Partial** | Catches IPs with abuse history; clean fresh residential/datacenter IPs return nothing. |
| WebRTC IP leak | `iceProbe` (client-side) | **Covered\*** | JS-only (finding #1). |

### Layer 3.5: DOM honeypots

| Technique (guide) | StyloBot signal / detector | Verdict | Notes |
|---|---|---|---|
| Path honeypots (`/.env`, `/.git`, `/wp-login`) | `HoneypotPathTagger` + `HoneypotLinkContributor` (Tier 1 always / Tier 2 probable) | **Covered** | Tier 1 = instant verified-bot 0.95, non-exemptable. Per-request, not learnable. |
| Beacon / canary replay across rotation | `BeaconContributor` + `BeaconCanaryGenerator` | **Covered** | Links rotated fingerprints via `beacon.original_fingerprint`. |
| Hidden form-field traps | `SbHoneypotTagHelper` + `HoneypotValidator` | **Partial** | Exists, but trivially skipped via DOM inspection, and no signal is written when the trap is *avoided* (only when tripped). |

### Layer 4: Behavioural ML

| Technique (guide) | StyloBot signal / detector | Verdict | Notes |
|---|---|---|---|
| Mouse Bezier-curve simulation | `mouseStats` → `ClientSideMouseAllIntegerCoords` / `MouseTimingCv` consumed by `HeuristicFeatureExtractor`, `UserAgentContributor`, `InconsistencyContributor` | **Partial\*** | Catches synthetic-mouse *tells* (Kameleo Chroma integer coords, low timing CV). Does **not** positively score Bezier human-likeness, and is absent for HTTP-only clients. A genuine sub-pixel Bezier simulator with irregular dt passes the mouse check. |
| Human-like timing injection (200-500ms jitter) | `BehavioralContributor`, `FrequencyFingerprintEncoder`, `ContentSequenceContributor` | **Covered** | Server-side timing entropy, burst ratio, autocorrelation rhythm, <20ms machine-speed transition, impossible-transition thresholds. |
| Periodic-loop jitter / rhythm-preserving rotation | `PeriodicityContributor` cross-session frequency match (0.85) | **Covered** | Autocorrelation survives moderate jitter. |
| Fresh-fingerprint-per-session rotation | Metastable identity (`IIdentityAnchorIndex`, L1/L2, drift) | **Gap** | Master switch `Identity.Enabled` defaults **false** (`IdentityOptions.cs:14`). Even enabled: per-request IP/TLS rotation breaks the L1 point lookup and each synthetic fingerprint lands in a separate centroid; no cross-fingerprint rotation-trail linking is implemented. This is the PerimeterX-style bypass. |
| Distributed low-and-slow (1 req/hr, rotating everything) | none effective | **Gap** | No cross-session velocity linking across identities; reputation decays before promotion. |
| Content-sequence mimicry (correct asset order, scraped HTML) | `ContentSequenceContributor` | **Partial** | Catches <20ms machine-speed ordering, but cannot verify the client actually rendered; injected human-paced ordering passes. |

### Vendor bypass recipes and tools (Anti-Bots / Libs / Cases)

| Item (guide) | Relevance to StyloBot | Verdict | Notes |
|---|---|---|---|
| Akamai / Cloudflare / DataDome / PerimeterX / Kasada / F5 vendor cookies | StyloBot replaces these; does not read their cookies | **N/A** | StyloBot is a primary anti-bot, not a bypass client. |
| Camoufox (Firefox below-CDP, "100% CF pass") | CDP traps evaded (it is below CDP); canvas/audio/webgl/webrtc/mouse still apply if JS runs | **Partial\*** | The CDP layer specifically does not catch Camoufox; other JS signals do. HTTP-only N/A. |
| CloakBrowser (49 C++ patches, reCAPTCHA 0.9) | Same class as Camoufox | **Partial\*** | Guide notes it *cannot* spoof WASM SIMD; StyloBot does not yet use SIMD (Gap above), so that lever is unused. |
| PatchRight / SeleniumBase UC / playwright-stealth | Stealth-framework `toString()` self-inspection tells | **Partial\*** | Stack-frame wrapper detection helps; framework-specific. |
| Managed residential browser APIs (Bright Data 98.44%, Zyte 93.14%) | Real browsers, clean residential IPs, real fingerprints | **Gap** | Only server-side behaviour + (disabled) identity can catch. Inherently hard for any detector. |
| CapSolver / 2captcha / Anti-Captcha | StyloBot PoW/JS challenge | **N/A / Partial** | A JS-executing headless browser solves StyloBot's PoW; solve-timing is a soft feedback signal on the *next* request, not a hard block. |

### Innov (2026 findings)

| Finding (guide) | StyloBot exposure | Verdict |
|---|---|---|
| CDP transparency patches removed by V8/Edge (flag-based CDP detection unreliable) | StyloBot uses runtime CDP *traps*, not the `navigator.webdriver` flag, so more durable; still erodes over time | **Partial** |
| WASM SIMD CPU fingerprint (un-spoofable by cloak browsers) | not collected | **Gap** |
| PerfectCanvas real-GPU hash harvesting | canvas hash alone is defeatable; StyloBot does not pair it with SIMD timing | **Partial** |
| CVE-2026-6770 IndexedDB ordering leak | not probed | **Gap** |
| SharedArrayBuffer 17x timer precision | not used (clamp-residue is) | **Partial** |
| Hyphenation-dictionary fork detection | not probed | **Gap** |

## Gap register (severity-ranked)

Severity = exploitability x prevalence. Remediation is generic per protocol/spec, never site-specific (per project rules).

### G1 (Critical): Transport fingerprint headers trusted without proxy gating
- **What:** `X-JA3-*`, `X-Client-TLS-*`, `X-HTTP2-*`, `X-QUIC-*`, `X-TCP-*` are read and converted to human/bot bias with no check that the request came from a trusted edge. Direct-HTTPS clients spoof a human fingerprint.
- **Residual StyloBot could read:** the real Kestrel TLS/ALPN handshake (`ITlsConnectionFeature`, `Request.Protocol`) is available and authoritative for the proxy-to-origin hop; the spoofed headers contradict it.
- **Remediation:** add a trusted-proxy allowlist (configured CIDRs / `ForwardedHeaders` known proxies). When the immediate peer is not on the allowlist, ignore `X-*` transport headers and fall back to live Kestrel metadata; optionally treat their *presence* from a non-proxy peer as a bot signal. Detectors: the four transport contributors; gate centrally so the policy is one place. Reconciles and supersedes memory `project_x_ja3_header_trust_unverified`.

### G2 (High): Entire JS fingerprint layer absent for HTTP-only clients
- **What:** The guide's primary bypass path (curl_cffi / Scrapy / XHR / mobile API) never executes `botdetection.js`, so canvas/webgl/audio/CDP/webrtc/mouse contribute nothing. Coverage degrades to TLS + headers + IP + server-side behaviour.
- **Residual:** the absence of any client fingerprint on a route that *served* the probe is itself a signal; `clientside.no_fingerprint_bias` exists but is a mild penalty.
- **Remediation:** (a) ensure the TLS-corpus checks in G5 are always live so the HTTP-only path still has a hard residual; (b) lean on server-side sequence/rhythm (already strong) and raise `no_fingerprint_bias` on document routes where the probe was injected but never beaconed back; (c) treat "page served, asset+probe never fetched, but API hit directly" as a sequence divergence.

### G3 (High): Fresh-fingerprint-per-session rotation
- **What:** Metastable identity (the layer designed to defeat this) is off by default and has no cross-fingerprint rotation-trail linking; per-request rotation lands each synthetic identity in its own centroid.
- **Remediation:** this is a roadmap item, not a config flip. Short term: document that `Identity.Enabled = true` is required to resist rotation and measure its cost. Medium term: implement cosine-neighbour rotation-trail walking across centroids (the "Merge via neighbour walking" described in architecture but not found wired). Links to memory `project_session_vectors`.

### G4 (High): Managed residential browser APIs
- **What:** Bright Data / Zyte serve real browsers on clean residential IPs with authentic fingerprints. No fingerprint or IP signal distinguishes them.
- **Remediation:** accept that this is behaviour-only; ensure session-sequence and inter-session velocity (G3) are the catch. No fingerprint-layer fix is honest here. Document as a known residual.

### G5 (Medium): Corpus-gated TLS checks silently skip when unloaded
- **What:** cipher-subset (the damru catch) and UA-vs-TLS version-delta only run when `_referenceIndex` is populated; an empty corpus disables them with no signal.
- **Remediation:** ship a baseline corpus as an embedded resource so the checks are never silently off; log a startup warning if the index is empty. Detector: `TlsFingerprintContributor`. Directly affects memory `project_bdf_cloak_scenarios_blocked`.

### G6 (Medium): Geographic coherence is single-vector
- **What:** StyloBot checks IP-geo vs Accept-Language vs known-bot-origin only; the guide describes 5-vector alignment (add TLS-locale, client timezone via JS, DNS-resolver geo).
- **Remediation:** add a timezone vector from the JS probe (when present) and widen the language-country map beyond ~7 countries in `MultiLayerCorrelation`. Low effort, additive.

### G7 (Medium): Missing un-spoofable JS fingerprint dimensions
- **What:** WASM SIMD CPU fingerprint, IndexedDB ordering, SAB high-precision timers, hyphenation-dictionary, extension probing are not collected. The guide specifically calls SIMD un-spoofable by cloak browsers, which is exactly where StyloBot's current canvas/audio hashes are weakest.
- **Remediation:** add a SIMD CPU probe and a SAB timer probe to `botdetection.js` and corresponding `clientside.*` signals. Only helps when the probe runs (so pairs with G2), but it is the highest-value addition against Camoufox/CloakBrowser. Largest code surface of the gaps.

### G8 (Medium): JS/PoW challenge bypassed by JS-executing headless
- **What:** Puppeteer/Playwright solve the PoW; timing/jitter/worker-count is a soft feedback signal next request, not a hard gate.
- **Remediation:** keep PoW as friction + feedback (its real value), and ensure the timing-feedback signals feed reputation with enough weight to matter on repeat. Detector: `ChallengeVerificationContributor`. Acknowledge PoW is friction, not proof.

### G9 (Low-Medium): DOM hidden-field honeypot is weak and silent-on-avoidance
- **What:** Trivially skipped via DOM inspection; emits no signal when avoided.
- **Remediation:** emit a weak human/neutral signal when the trap is present-and-correctly-left-empty *with* other human evidence, and consider randomised field names per render. Detector: `SbHoneypotTagHelper` / `HoneypotValidator`.

### G10 (Low): CDP-trap durability erosion
- **What:** Browser vendors removing CDP transparency patches weakens flag-based detection; StyloBot's runtime traps are more durable but not immune.
- **Remediation:** monitor; the runtime `console.debug` getter + `toString()`-counter traps are the right design. No action beyond tracking the upstream change.

## Reconciliation with prior findings (memory)

- **`project_x_ja3_header_trust_unverified`** : confirmed and broadened. The trust gap is not X-JA3-only; it spans all four transport detectors. Folded into G1, which should supersede the standalone note.
- **`project_bdf_cloak_scenarios_blocked` (damru/Multilogin scoring 0.07)** : the *detection* for damru tells exists and is wired (ethernet-on-mobile-UA and empty-getVoices in `InconsistencyContributor`, no-srflx in `iceProbe`, doc/asset cipher-subset in `TlsFingerprintContributor`). The 0.07 rig score is therefore a harness/forwarded-header issue (the X-JA3 path not producing `tls.ja3_string` under the rig), not absent capability. G5 (corpus must be loaded) is the most likely contributor; the rig also needs the trusted-header path G1 introduces. The cloak detections themselves are present, contradicting any read of "no coverage".
- **`reference_damru_cloak_browser`** : the documented tells (ethernet+mobile UA, no ICE, empty getVoices, doc/asset JA3 split) are all implemented. The Runtime-flap tell maps to the CDP traps. Durability ranking holds; SIMD (G7) is the next rung the cloak browsers cannot climb.
- **`project_session_vectors`** : the server-side behavioural layer is as described and strong. The unrealised piece is cross-fingerprint rotation-trail linking (G3).

## One-line takeaway

StyloBot's server-side behavioural and sequence detection is solid and its in-browser fingerprint probe is genuinely deep; the real exposure is structural, not a missing detector list: a header-trust hole that lets a direct client spoof a *human* fingerprint (G1), and the fact that the strongest layer evaporates against the guide's most-recommended HTTP-only path (G2), with fresh-fingerprint rotation (G3) and managed residential browsers (G4) as the hard residuals that only behaviour (and a currently-disabled identity layer) can address.
