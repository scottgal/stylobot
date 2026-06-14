# Coverage-gap remediation plan (staged)

Companion to [2026-06-13-web-scraping-guide-coverage-audit.md](2026-06-13-web-scraping-guide-coverage-audit.md). Gaps are referenced as G1-G10 from that document.

**Sequencing principle:** trust before measurement before expansion before roadmap. You cannot evaluate any other change while transport signals are spoofable (G1) and the damru corpus may be silently unloaded (G5), and the BDF rig will keep under-scoring until both are fixed. So Stage 0 is a hard prerequisite for trusting the verification of every later stage.

Each stage ends with a verification gate. The substantial items (G1, G3, G7) should each get their own brainstorm -> writing-plans cycle when picked up; this document is the roadmap, not the per-feature spec.

---

## Stage 0: Trust and rig integrity (prerequisite)

Goal: make transport signals trustworthy and the damru catch always-live, so the rig measures reality and an attacker can no longer spoof a human fingerprint.

- [ ] **0.1 (G1) Central trusted-proxy gate for transport headers.**
  Add one gating helper that the four transport contributors (`TlsFingerprintContributor`, `Http2FingerprintContributor`, `Http3FingerprintContributor`, `TcpIpFingerprintContributor`) call before trusting any `X-JA3-*` / `X-Client-TLS-*` / `X-HTTP2-*` / `X-QUIC-*` / `X-TCP-*` header. Source of truth: configured trusted-proxy CIDRs (reuse `ForwardedHeaders` known-proxy config if present). When the immediate peer is not trusted, ignore those headers and fall back to live Kestrel metadata (`ITlsConnectionFeature`, `Request.Protocol`).
  - Decision to settle in its own brainstorm: should presence of these headers from an untrusted peer be a *bot* signal, or just ignored? (Lean: weak bot signal; spoofing edge headers is not innocent.)
- [ ] **0.2 (G5) Ship a baseline TLS reference corpus as an embedded resource.**
  Guarantee `TlsFingerprintContributor._referenceIndex` is non-empty at startup so cipher-subset (damru) and UA-vs-TLS version-delta checks never silently skip. Log a Warning if the index is empty after load.
- [ ] **0.3 Verification gate.**
  - Direct-HTTPS request with spoofed `X-JA3-Hash: <known-Chrome>` no longer earns the `-0.15` human bias (new test).
  - Startup with no external corpus still runs the damru cipher-subset check (new test).
  - Re-run the BDF cloak scenarios; confirm the damru/Multilogin score lifts off 0.07. Update memory `project_bdf_cloak_scenarios_blocked` with the result.

---

## Stage 1: Cheap server-side hardening (no new client probe)

Goal: close the low-effort server-side gaps. All independent of each other; can be parallelised. None touch `botdetection.js`.

- [ ] **1.1 (G2a) Raise the cost of the HTTP-only path.**
  On document routes where the probe was injected but never beaconed back, and the client then hits an API directly, treat it as a sequence divergence and raise `clientside.no_fingerprint_bias` from a mild penalty to a meaningful one. Wire through `ContentSequenceContributor` / `ClientSideContributor`. Careful not to penalise legitimate API-first clients (gate on "probe was served for this fingerprint").
- [ ] **1.2 (G6 part) Widen the geo language-country map.**
  Extend the ~7-country `MultiLayerCorrelation` language-vs-country mapping to cover the major language families. Pure data/config; no architecture change. (Timezone vector deferred to Stage 2 with the probe work.)
- [ ] **1.3 (G8) Make challenge-timing feedback matter on repeat.**
  Ensure `ChallengeVerificationContributor` solve-timing/jitter/worker-count signals feed reputation with enough weight that a fast, regular, low-worker solver accrues toward ConfirmedBad across requests. Keep PoW as friction + feedback (its real role), not as proof.
- [ ] **1.4 (G9) Harden the DOM hidden-field honeypot.**
  Randomise trap field names per render; emit a weak human/neutral signal when the trap is present-and-correctly-empty *with* corroborating human evidence (so avoidance is informative, not silent). `SbHoneypotTagHelper` / `HoneypotValidator`.
- [ ] **1.5 Verification gate.** New unit tests per item; confirm no false-positive regression on the human-traffic BDF scenarios.

---

## Stage 2: JS probe expansion (the cloak-browser lever)

Goal: add the fingerprint dimensions that Camoufox/CloakBrowser cannot spoof. All touch `botdetection.js` + `BrowserFingerprintResult`/`Analyzer` + new `clientside.*` signal keys + the 5-file detector path, so batch them. Note: only helps when the probe runs, so value is conditional on Stage 1.1 raising the no-probe cost.

- [ ] **2.1 (G7) WASM SIMD CPU fingerprint probe.** Highest-value addition; the guide flags it as un-spoofable by cloak browsers. New probe in `botdetection.js`, new `clientside.cpu_*` signals, scoring in the analyzer.
- [ ] **2.2 (G7) SharedArrayBuffer high-precision timer probe.** Complements the existing `performance.now()` clamp-residue. Note SAB requires cross-origin-isolation headers; confirm the probe degrades gracefully when unavailable.
- [ ] **2.3 (G6 part) Client timezone vector.** Feed `Intl.DateTimeFormat().resolvedOptions().timeZone` into the geo-coherence check (IP-geo vs timezone vs Accept-Language). Moves geo from single-vector toward the guide's multi-vector alignment.
- [ ] **2.4 (deferred, optional) IndexedDB-ordering / hyphenation-dictionary probes.** Lower value; only if a target population shows the relevant cloak forks. Capture as backlog, do not build speculatively.
- [ ] **2.5 Verification gate.** Probe runs and beacons on a real Chrome/Firefox/Safari; SIMD signal distinguishes a known cloak-browser profile from stock Chrome; privacy-tool legitimacy markers still suppress false positives.

---

## Stage 3: Identity and rotation resistance (roadmap track)

Goal: address the hard residuals: fresh-fingerprint-per-session rotation and managed residential browsers. This is the largest, longest track and needs its own brainstorm + plan before any code.

- [ ] **3.1 (G3) Measure `Identity.Enabled = true` cost and resistance.** Turn the metastable identity layer on in a measurement deployment; quantify per-request latency and how well L1/L2 + drift resist rotation today. Establishes the baseline before building new linking.
- [ ] **3.2 (G3) Cross-fingerprint rotation-trail linking.** Implement cosine-neighbour walking across centroids (the "Merge via neighbour walking" the architecture describes but that is not wired) so N synthetic fingerprints rotating per session resolve toward one entity via velocity/drift trajectory. This is the core unbuilt piece.
- [ ] **3.3 (G4) Document managed-residential-browser residual.** No honest fingerprint/IP fix exists; record that session-sequence + inter-session velocity (3.2) are the only catch, set expectations in docs.
- [ ] **3.4 Verification gate.** A rotation harness (fresh IP+TLS+UA per session, behaviour held constant) resolves to a single entity once 3.2 is on; latency budget from 3.1 is acceptable.

---

## Stage 4: Tracking only

- [ ] **4.1 (G10) Monitor CDP-trap durability.** The runtime `console.debug` getter + `toString()`-counter traps are the right design; track upstream V8/Edge changes to CDP transparency. No build work unless the traps stop firing.

---

## Dependency summary

```
Stage 0 (G1, G5)  ──prerequisite──►  everything (trust baseline + rig)
Stage 1 (G2a,G6a,G8,G9)             independent, parallelisable, server-side
Stage 2 (G7,G6b)                    needs Stage 1.1 to be worth it; batches botdetection.js edits
Stage 3 (G3,G4)                     own brainstorm+plan; longest; behaviour-only residuals
Stage 4 (G10)                       tracking
```

Recommended order to start: **0.1 -> 0.2 -> 0.3 gate**, then pick up Stage 1 items in parallel while Stage 3.1 measurement runs in the background.
