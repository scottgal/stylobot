# Marketing facts — v8.5 (verified delta)

Produced by `reldocs-` for `overview-`/the commercial marketing-site owner. This repo (FOSS `stylobot`)
is the source of truth for these numbers; **do not edit `mostlylucid.stylobot.website` from this repo** —
this file only hands off the verified facts.

## 1. Authoritative detector count: **67**, not 49/50/57/63/64

The FOSS detection engine currently ships **67 `IDetectorAtom` implementations** under
`src/Mostlylucid.BotDetection/Orchestration/Atoms/`. This is the number to use everywhere on the site
("detectors", "detector atoms", "bot signals" — pick one term and use it consistently).

**Method (reproducible):**

```bash
grep -rlE "class\s+\w+\s*:\s*(DetectorAtomBase|.*\bIDetectorAtom\b)" \
  src/Mostlylucid.BotDetection/Orchestration/Atoms --include="*.cs" | wc -l
# => 67
```

Cross-checked against the DI registration list in `BotDetectionOrchestrator.cs`
(`AddDetectorAtom<T>()` calls, plus `RequestHydratorAtom` registered separately) — same 67.

Of those 67: **63 have a YAML manifest** under `Orchestration/Manifests/detectors/` (tunable via
config); **4 are config-free by design** (`BrowserCharConsistencyAtom`, `RegistryClientSensor`,
`SessionModeResolverAtom`, `WebBotAuthApprovalAtom`). No manifest is orphaned.

"Atom" and "contributor" are the **same concept, two generations** — `IContributingDetector`
("contributor") was the pre-v8 blackboard-era interface; `IDetectorAtom` ("atom") replaced it entirely
in the v8 wave-orchestrator rewrite and `IContributingDetector` no longer exists in code. Any site copy
still saying "contributors" is using dead terminology, not a different number. **67 is the one number
for public copy** — "detectors" or "detector atoms" are both fine as the public-facing term; there is
no other registration/contributor figure that means something different for marketing purposes.

### Full list of the 67 detector atoms (`Orchestration/Atoms/`)

AccountTakeoverAtom, AiAtom, AiScraperAtom, BehavioralAtom, BehavioralWaveformAtom,
BrowserCharConsistencyAtom, BrowserModeClassifierAtom, CacheBehaviorAtom, ChallengeVerificationAtom,
ClaimedIdentityAtom, ClickFraudAtom, ClientSideAtom, ClusterAtom, ContentSequenceAtom,
CookieBehaviorAtom, CveFingerprintAtom, CveProbeAtom, EndpointHistoryAtom, FastPathReputationAtom,
FediverseDomainAtom, FingerprintApprovalAtom, FingerprintMatchAtom, FingerprintPriorAtom,
GeoChangeAtom, HaxxorAtom, HeaderAtom, HeaderCorrelationAtom, HealthEndpointAtom,
HealthEndpointReconAtom, HeuristicAtom, HeuristicLateAtom, HoneypotLinkAtom, Http2FingerprintAtom,
Http3FingerprintAtom, IdentityChangeAtom, IdentityVectorAtom, InconsistencyAtom, IntentAtom, IpAtom,
LlmAtom, MultiLayerCorrelationAtom, PeriodicityAtom, PiiQueryStringAtom, PoolCollisionAtom,
ProjectHoneypotAtom, ReactivePatternAtom, RegistryClientSensor, ReputationBiasAtom,
RequestHydratorAtom, ResourceWaterfallAtom, ResponseBehaviorAtom, SecurityToolAtom,
SessionModeResolverAtom, SessionVectorAtom, SignatureAtom, SimilarityAtom, StreamAbuseAtom,
TcpIpFingerprintAtom, ThreatIntelAtom, TimeAtom, TlsFingerprintAtom, TransportProtocolAtom,
UserAgentAtom, VerifiedBotAtom, VerifiedBotInlineAtom, VersionAgeAtom, WebBotAuthApprovalAtom.

**Registration mechanism** (for reproducibility): `BotDetectionOrchestrator.AddNativeDetectorAtoms()`
in `src/Mostlylucid.BotDetection/Orchestration/Atoms/BotDetectionOrchestrator.cs` is a literal,
hand-maintained list — one `services.AddDetectorAtom<TAtom>()` call per class above (grouped by
taxonomy role), plus `RequestHydratorAtom` registered one line above via
`services.AddSingleton<IDetectorAtom, RequestHydratorAtom>()`. It is not a reflection/assembly scan, so
the count only changes when a line is added or removed here — cross-checked against the grep count in
section 1 above, both agree at 67.

### Stale counts found in FOSS docs, now corrected in this repo (this branch)

- `CLAUDE.md` — "63 priority-ordered detector atoms" / "~27 Wave 0 atoms of 63 total" / "All 64 atom
  implementations" / "all 57 contributors" → all now say 67 (or 63-manifests-of-67, where the
  manifest/atom distinction matters).
- `README.md` (repo root) — "57 detectors" (×3 places) → 67.
- `src/Mostlylucid.BotDetection/README.md` — "57 contributors" (×3 places) → 67 detector atoms.
- `src/Mostlylucid.BotDetection/docs/quickstart.md` — "57 contributors" (×3 places) → 67 detector atoms.
- `docs/articles/stylobot-fingerprint.md` — "roughly 50 contributors" → 67 detector atoms.
- `docs/RELEASE_NOTES.md:150` still says "45 detectors" under the **v6.0.0-alpha historical entry** —
  left untouched deliberately (changelogs/release notes are append-only history, correct as of that
  release, not a current-state claim).

### Known stale count in the commercial/marketing side (flagging, not fixing — not my repo)

My own memory of prior marketing-copy conventions says the website has used **"49 detectors"** in
places. That number was already stale before this release (the true FOSS count has been 63-67 across
recent releases) and is now off by 18. Whoever owns `mostlylucid.stylobot.website` copy should grep the
site for `49 detector`, `57 detector`, `57 contributor` and replace with **67**.

## 2. v8.5 feature highlights worth surfacing on the site

Ranked roughly by customer-visible impact; full detail in the FOSS root
[`CHANGELOG.md`](../CHANGELOG.md#850---2026-07-25).

1. **Dashboard performance.** The real-time dashboard's read path now flows through a materializer
   content-cache instead of hitting the database (or, for the hosted/website dashboard, a live
   cross-service call) on every page render — closes a p99=10 second worst-case load on the Traffic
   page. Safe to pitch as "dashboard loads instantly, even under load" without overclaiming a specific
   number (the fix is architectural, not a benchmarked SLA).
2. **Per-domain accuracy fix.** A real bug where every request's `domain` attribution silently fell
   back to `"unknown"` is fixed — this is what feeds the site's own per-domain/multi-site licensing
   split, so it's relevant to any "manage multiple domains" claim on the pricing/dashboard pages.
3. **Endpoint & signature detail pages.** Both now have real, linkable, filterable detail views (MODE/
   METHOD/STATUS filters, a path-shape classifier) instead of the old inline-swap panels — worth a
   screenshot refresh if the site has dashboard screenshots from before this release.
4. **Upstream health monitoring now actually works.** The site-health card previously always showed
   "healthy" regardless of real upstream errors (dead code, never wired to traffic) — now genuinely
   reflects 5xx/4xx/429 rates. If the site claims real upstream-health monitoring as a feature, it is
   now true; before v8.5 it silently wasn't.
5. **Gateway hardening.** The gateway self-raises its file-descriptor limit at boot, so it survives a
   connection burst without an external systemd unit doing that job. Relevant to any "production-grade
   / battle-tested edge" framing.

## 3. Behavior change worth a support/docs note (not a headline, but customer-visible)

**FOSS no longer supports any runtime config hot-reload**, including the (undocumented, never-intended)
`POST /admin/reload` endpoint, which is removed. An operator who was editing a mounted
`appsettings.json` and expecting it to apply live now needs a restart. Commercial hot-reload (via the
Postgres-backed config editor) is unaffected — that was already a separate mechanism
(`IConfigurationOverrideSource`), not FOSS's `IOptionsMonitor` path. If any FOSS-facing docs/site copy
promised live config editing without a restart, that claim needs to go — it was never true for FOSS,
and is now enforced in code rather than just true in practice.
